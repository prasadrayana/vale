// Make local transformations on operands and overloaded terms:
//   - resolve overloading
//   - turn operands into appropriate ghost variables
//   - turn references to operands into appropriate ghost variables
//   - insert some diagnostic assertions for {:refined true}
// Mostly, this is about changing expressions and arguments

module Transform

open Ast
open Ast_util
open Parse
open Microsoft.FSharp.Math

let assumeUpdates = ref 0

type id_local = {local_in_param:bool; local_exp:exp; local_typ:typ option} // In parameters are read-only and refer to old(state)
type id_info =
| GhostLocal of typ option
| ProcLocal of id_local
| ThreadLocal of id_local
| InlineLocal
| OperandLocal of inout * string * typ
| StateInfo of string * exp list * typ

type env =
  {
    funs:Map<id, fun_decl>;
    procs:Map<id, proc_decl>;
    raw_procs:Map<id, proc_decl>;
    ids:Map<id, id_info>;
    mods:Map<id, bool>;
    state:exp;
    abstractOld:bool; // if true, x --> va_old_x in abstract lemma
  }

let empty_env:env =
  {
    funs = Map.empty;
    procs = Map.empty;
    raw_procs = Map.empty;
    ids = Map.empty;
    mods = Map.empty;
    state = EVar (Reserved "s");
    abstractOld = false;
  }

let vaApp (s:string) (es:exp list):exp = EApply (Reserved s, es)

let vaAppOp (prefix:string) (t:typ) (es:exp list):exp =
  match t with
  | TName (Id x) -> vaApp (qprefix prefix x) es
  | _ -> err "operands must have simple named types"

let vaEvalOp (xo:string) (t:typ) (state:exp) (e:exp):exp =
  match t with
  | TName (Id x) -> vaApp (qprefix ("eval_" + xo + "_") x) [state; e]
  | _ -> err "operands must have simple named types"

let old_id (x:id) = Reserved ("old_" + (string_of_id x))
let prev_id (x:id) = Reserved ("prev_" + (string_of_id x))

let tBool = TName (Reserved "bool")
let tInt = TName (Reserved "int")
let tOperand xo = TName (Reserved xo)
let tState = TName (Reserved "state")
let tCode = TName (Reserved "code")
let tCodes = TName (Reserved "codes")

let exp_abstract (useOld:bool) (e:exp):exp =
  let c e = match e with EOp (Uop UConst, [e]) -> e | _ -> e in
  map_exp (fun e -> match e with EOp (RefineOp, [e1; e2; e3]) -> Replace (if useOld then c e2 else c e1) | _ -> Unchanged) e

let exp_refined (e:exp):exp =
  map_exp (fun e -> match e with EOp (RefineOp, [e1; e2; e3]) -> Replace e3 | _ -> Unchanged) e

let stmts_abstract (useOld:bool) (ss:stmt list):stmt list =
  map_stmts (exp_abstract useOld) (fun _ -> Unchanged) ss

let stmts_refined (ss:stmt list):stmt list =
  map_stmts exp_refined (fun _ -> Unchanged) ss

let rec env_map_exp (f:env -> exp -> exp map_modify) (env:env) (e:exp):exp =
  map_apply_modify (f env e) (fun () ->
    let r = env_map_exp f env in
    match e with
    | ELoc (loc, e) -> try ELoc (loc, r e) with err -> raise (LocErr (loc, err))
    | EVar (Reserved "this") -> env.state
    | EVar _ | EInt _ | EReal _ | EBitVector _ | EBool _ | EString _ -> e
    | EBind (b, es, fs, ts, e) ->
        let es = List.map r es in
        let env = {env with ids = List.fold (fun env (x, t) -> Map.add x (GhostLocal t) env) env.ids fs} in
        let r = env_map_exp f env in
        EBind (b, es, fs, List.map (List.map r) ts, r e)
    | EOp (Uop UOld, [e]) ->
        let env = {env with state = EVar (Reserved "old_s"); abstractOld = true} in
        let r = env_map_exp f env in
        r e
    | EOp (Bop BOldAt, [es; e]) ->
        let env = {env with state = es} in
        let r = env_map_exp f env in
        r e
    | EOp (op, es) -> EOp (op, List.map r es)
    | EApply (x, es) -> EApply (x, List.map r es)
  )

let rec env_map_stmt (fe:env -> exp -> exp) (fs:env -> stmt -> (env * stmt list) map_modify) (env:env) (s:stmt):(env * stmt list) =
  map_apply_modify (fs env s) (fun () ->
    let fee = fe env in
    let r = env_map_stmt fe fs env in
    let rs = env_map_stmts fe fs env in
    match s with
    | SLoc (loc, s) -> try let (env, ss) = r s in (env, List.map (fun s -> SLoc (loc, s)) ss) with err -> raise (LocErr (loc, err))
    | SLabel x -> (env, [s])
    | SGoto x -> (env, [s])
    | SReturn -> (env, [s])
    | SAssume e -> (env, [SAssume (fee e)])
    | SAssert (inv, e) -> (env, [SAssert (inv, fee e)])
    | SCalc (oop, contents) -> (env, [SCalc (oop, List.map (env_map_calc_contents fe fs env) contents)])
    | SSplit -> (env, [s])
    | SVar (x, t, g, a, eOpt) ->
      (
        let info =
          match g with
          | XAlias (AliasThread, e) -> ThreadLocal {local_in_param = false; local_exp = e; local_typ = t}
          | XAlias (AliasLocal, e) -> ProcLocal {local_in_param = false; local_exp = e; local_typ = t}
          | XGhost -> GhostLocal t
          | XInline -> InlineLocal
          | (XOperand _ | XPhysical | XState _) -> err ("variable must be declared ghost, {:local ...}, or {:register ...} " + (err_id x))
          in
        let ids = Map.add x info env.ids in
        ({env with ids = ids}, [SVar (x, t, g, map_attrs fee a, mapOpt fee eOpt)])
      )
    | SAssign (xs, e) ->
        let ids = List.fold (fun ids (x, dOpt) -> match dOpt with None -> ids | Some (t, _) -> Map.add x (GhostLocal t) ids) env.ids xs in
        ({env with ids = ids}, [SAssign (xs, fee e)])
    | SBlock b -> (env, [SBlock (rs b)])
    | SIfElse (g, e, b1, b2) -> (env, [SIfElse (g, fee e, rs b1, rs b2)])
    | SWhile (e, invs, ed, b) ->
        (env, [SWhile (fee e, List_mapSnd fee invs, mapSnd (List.map fee) ed, rs b)])
    | SForall (xs, ts, ex, e, b) ->
        let env = {env with ids = List.fold (fun env (x, t) -> Map.add x (GhostLocal t) env) env.ids xs} in
        let fee = fe env in
        let rs = env_map_stmts fe fs env in
        (env, [SForall (xs, List.map (List.map fee) ts, fee ex, fee e, rs b)])
    | SExists (xs, ts, e) ->
        let env = {env with ids = List.fold (fun env (x, t) -> Map.add x (GhostLocal t) env) env.ids xs} in
        let fee = fe env in
        (env, [SExists (xs, List.map (List.map fee) ts, fee e)])
  )
and env_map_stmts (fe:env -> exp -> exp) (fs:env -> stmt -> (env * stmt list) map_modify) (env:env) (ss:stmt list):stmt list =
  List.concat (snd (List_mapFoldFlip (env_map_stmt fe fs) env ss))
and env_map_calc_contents (fe:env -> exp -> exp) (fs:env -> stmt -> (env * stmt list) map_modify) (env:env)  (cc:calcContents) =
  match cc with
  | CalcLine e -> CalcLine (fe env e)
  | CalcHint (oop, ss) -> CalcHint (oop, env_map_stmts fe fs env ss)

let env_next_stmt (env:env) (s:stmt):env =
  let (env, _) = env_map_stmt (fun _ e -> e) (fun _ _ -> Unchanged) env s in
  env

let match_proc_args (env:env) (p:proc_decl) (rets:lhs list) (args:exp list):((pformal * lhs) list * (pformal * exp) list) =
  let (nap, nac) = (List.length p.pargs, List.length args) in
  let (nrp, nrc) = (List.length p.prets, List.length rets) in
  if nap <> nac then err ("in call to " + (err_id p.pname) + ", expected " + (string nap) + " argument(s), found " + (string nac) + " argument(s)") else
  if nrp <> nrc then err ("procedure " + (err_id p.pname) + " returns " + (string nrp) + " value(s), call expects " + (string nrc) + " return value(s)") else
  (List.zip p.prets rets, List.zip p.pargs args)

///////////////////////////////////////////////////////////////////////////////
// Resolve overloading

let rec resolve_overload_assign (env:env) (lhss:lhs list) (e:exp):(lhs list * exp) =
  let isProcVar x =
    match Map.tryFind x env.ids with
    | None -> false
    | Some (GhostLocal _ | InlineLocal) -> false
    | Some (ProcLocal _ | ThreadLocal _ | OperandLocal _ | StateInfo _) -> true
    in
  let rewriteLhs e =
    match lhss with
    | [(x, None)] when isProcVar x ->
      (
        match Map.tryFind (Operator ":=") env.procs with
        | Some {pargs = [(_, _, _, In, _)]; prets = [_]; pattrs = attrs} ->
            let e = EApply (attrs_get_id (Reserved "alias") attrs, [e]) in
            resolve_overload_assign env lhss e
        | Some {pargs = [(_, _, _, Out, _); (_, _, _, In, _)]; prets = []; pattrs = attrs} ->
            let e = EApply (attrs_get_id (Reserved "alias") attrs, [EVar x; e]) in
            resolve_overload_assign env [] e
        | _ -> err ("operator ':=' must be overloaded to assign to variable " + (err_id x))
      )
    | _ -> (lhss, e)
    in
  match (lhss, e) with
  | (_, ELoc (loc, e)) ->
      try let (lhss, e) = resolve_overload_assign env lhss e in (lhss, ELoc (loc, e))
      with err -> raise (LocErr (loc, err))
  | ([], EOp (Uop (UCustomAssign op), [e])) ->
    (
      match Map.tryFind (Operator op) env.procs with
      | Some {pargs = [(_, _, _, InOut, _)]; prets = []; pattrs = attrs} ->
          let e = EApply (attrs_get_id (Reserved "alias") attrs, [e]) in
          resolve_overload_assign env lhss e
      | _ -> err ("operator '" + op + "' must be overloaded to use as a postfix operator")
    )
  | ([(x, None)], EOp (Uop (UCustomAssign op), [e])) ->
    (
      match Map.tryFind (Operator op) env.procs with
      | Some {pargs = [(_, _, _, InOut, _); (_, _, _, In, _)]; prets = []; pattrs = attrs} ->
          let e = EApply (attrs_get_id (Reserved "alias") attrs, [EVar x; e]) in
          resolve_overload_assign env [] e
      | _ -> err ("operator '" + op + "' must be overloaded to use as an assignment operator")
    )
  | (_, EApply(x, es)) ->
    (
      match Map.tryFind x env.procs with
      | None -> rewriteLhs e
      | Some p -> (lhss, e)
    )
  | _ -> rewriteLhs e

let resolve_overload_stmt (env:env) (s:stmt):(env * stmt list) =
  let rec fs (env:env) (s:stmt):(env * stmt list) map_modify =
    match s with
    | SAssign (lhss, e) ->
        let (lhss, e) = resolve_overload_assign env lhss e in
        Replace (env, [SAssign (lhss, e)])
    | _ -> Unchanged
    in
  env_map_stmt (fun _ e -> e) fs env s
let resolve_overload_stmts (env:env) (ss:stmt list):stmt list =
  List.concat (snd (List_mapFoldFlip resolve_overload_stmt env ss))

///////////////////////////////////////////////////////////////////////////////
// Propagate variables through state via assumes (if requested)

(*
// Currently only works for straight-line code (no if/else or while)
let assume_updates_stmts (env:env) (args:pformal list) (rets:pformal list) (ss:stmt list) (specs:spec list):stmt list =
  if !assumeUpdates = 0 then ss else
  let thisAt i = Reserved ("assume_this_" + (string i)) in
  let setPrev (i:int) (prev:Map<id, int>) (xs:id list):(int * Map<id, int> * stmt list) =
    match xs with
    | [] -> (i, prev, [])
    | _::_ ->
        let ss = [SVar (thisAt i, Some tState, XGhost, [], Some (EVar (Reserved "this")))] in
        let prev = List.fold (fun prev x -> Map.add x i prev) prev xs in
        (i + 1, prev, ss)
    in
  let genAssume (env:env) (prev:Map<id, int>) (x:id):stmt list =
    match Map.tryFind x env.ids with
    | None | Some (GhostLocal _) -> []
    | Some (OperandLocal _ | ThreadLocal _ | ProcLocal _ | InlineLocal | StateInfo _) ->
        let old_state = match Map.tryFind x prev with None -> EOp (Uop UOld, [EVar (Reserved "this")]) | Some i -> EOp (Bop BOldAt, [EVar (thisAt i); EVar (Reserved "this")]) in
//        let old_x = match Map.tryFind x prev with None -> EOp (Uop UOld, [EVar x]) | Some i -> EOp (Bop BOldAt, [EVar (thisAt i); EVar x]) in
        let eq = EApply (Reserved "eq_ops", [old_state; EVar (Reserved "this"); (EOp (Uop UToOperand, [EVar x]))]) in
        [(if !assumeUpdates = 1 then SAssume eq else SAssert (NotInv, eq))]
    in
  let f (env:env, i:int, prev:Map<id, int>, sss:stmt list list) (s:stmt) =
    let rec r s =
      match s with
      | SLoc (loc, s) ->
          let (i, prev, ss) = r s in
          (i, prev, List.map (fun s -> SLoc (loc, s)) ss)
      | (SLabel _ | SGoto _ | SReturn | SSplit) -> (i, prev, [s])
      | SVar (x, t, (XGhost | XInline | XOperand | XPhysical | XState _), a, eOpt) ->
          (i, prev, [s])
      | SVar (x, t, XAlias _, a, eOpt) ->
          let (i, prev, ss) = setPrev i prev [x] in
          (i, prev, [s] @ ss)
      | SAssign (lhss, e) ->
        (
          match skip_loc e with
          | EApply (x, es) when Map.containsKey x env.procs ->
              let xfs = Set.toList (free_vars_stmt s) in
              let ssAssume = List.collect (genAssume env prev) xfs in
              let (mrets, margs) = match_proc_args env (Map.find x env.procs) lhss es in
              let xrets = List.map (fun ((_, _, g, _, _), (x, _)) -> (EVar x, Out, g)) mrets in
              let xargs = List.map (fun ((_, _, g, io, _), e) -> (skip_loc e, io, g)) margs in
              let fx e_io_g =
                match e_io_g with
                | (EVar x, (Out | InOut), (XOperand | XAlias _ | XInline)) -> [x]
                | _ -> []
                in
              let xsOut = List.collect fx (xrets @ xargs) in
              let (i, prev, ssSet) = setPrev i prev xsOut in
              (i, prev, ssAssume @ [s] @ ssSet)
          | _ ->
              let xfs = Set.toList (free_vars_stmt s) in
              (i, prev, (List.collect (genAssume env prev) xfs) @ [s])
        )
      | (SAssume _ | SAssert _ | SForall _ | SExists _) ->
          let xfs = Set.toList (free_vars_stmt s) in
          (i, prev, (List.collect (genAssume env prev) xfs) @ [s])
      | (SIfElse _ | SWhile _) ->
          // Not yet supported
          (i, prev, [s])
      in
    let (i, prev, ss) = r s in
    let sss = ss::sss in
    let env = env_next_stmt env s in
    (env, i, prev, sss)
  in
  let (env, i, prev, sssRev) = List.fold f (env, 0, Map.empty, []) ss in
  let ss = List.concat (List.rev sssRev) in
  let enss = List.collect (fun s -> match s with Ensures e -> [s] | _ -> []) specs in
  let globals = List.collect (fun (x, info) -> match info with ThreadLocal _ -> [x] | _ -> []) (Map.toList env.ids) in
  let xArgs = List.collect (fun (x, _, g, io, _) -> match (g, io) with ((XOperand | XAlias _ | XInline), _ (* TODO? InOut | Out *)) -> [x] | _ -> []) args in
  let xRets = List.collect (fun (x, _, g, io, _) -> match (g, io) with ((XOperand | XAlias _ | XInline), _) -> [x] | _ -> []) rets in
  let xfs = Set.toList (Set.unionMany [Set.ofList globals; Set.ofList xArgs; Set.ofList xRets; free_vars_specs enss]) in
  let ensAssume = List.collect (genAssume env prev) xfs in
  ss @ ensAssume
*)

///////////////////////////////////////////////////////////////////////////////
// Rewrite variables

let stateGet (env:env) (x:id):exp =
  match Map.find x env.ids with
  | StateInfo (prefix, es, _) -> vaApp ("get_" + prefix) (es @ [env.state])
  | _ -> internalErr "stateGet"

let refineOp (env:env) (io:inout) (x:id) (e:exp):exp =
  let abs_x = match (io, env.abstractOld) with (In, _) | (InOut, true) -> (old_id x) | (Out, _) | (InOut, false) -> x in
  EOp (RefineOp, [EVar x; EVar abs_x; e])

let rewrite_state_info (env:env) (x:id) (prefix:string) (es:exp list):exp =
  match Map.tryFind x env.mods with
  | None -> err ("variable " + (err_id x) + " must be declared in procedure's reads clause or modifies clause")
  | Some readWrite -> refineOp env (if readWrite then InOut else In) x (stateGet env x)

let rec rewrite_vars_arg (g:ghost) (asOperand:string option) (io:inout) (env:env) (e:exp):exp =
  let rec fe (env:env) (e:exp):exp map_modify =
    let codeLemma e = e
//      match asOperand with
//      | None -> e
//      | Some xo -> EOp (CodeLemmaOp, [vaApp "op" [e]; e]) // REVIEW -- should be more principled
      in
    let constOp e =
      let ec = EOp (Uop UConst, [e]) in
      match asOperand with
      | None -> ec
      | Some xo -> codeLemma (EOp (RefineOp, [ec; ec; vaApp ("const_" + xo) [e]]))
      in
    let operandProc xa io =
      let xa = string_of_id xa in
      match io with
      | In | InOut -> Id (xa + "_in")
      | Out -> Id (xa + "_out")
      in
    match (g, skip_loc e) with
    | (_, EVar x) when Map.containsKey x env.ids ->
      (
        match Map.find x env.ids with
        // TODO: check for incorrect uses of old
        | GhostLocal _ -> Unchanged
        | InlineLocal -> (match g with NotGhost -> Replace (constOp e) | Ghost -> Unchanged)
        | OperandLocal (opIo, xo, t) ->
          (
            match g with
            | Ghost -> Replace (refineOp env opIo x (vaEvalOp xo t env.state e))
            | NotGhost ->
              (
                match asOperand with
                | None -> Unchanged
                | Some xoDst ->
                    let e = if xo = xoDst then e else vaApp ("coerce_" + xo + "_to_" + xoDst) [e] in
                    Replace (EOp (OperandArg (x, xo, t), [e]))
              )
          )
        | ThreadLocal {local_in_param = inParam; local_exp = e; local_typ = t} | ProcLocal {local_in_param = inParam; local_exp = e; local_typ = t} ->
            if inParam && io <> In then err ("variable " + (err_id x) + " must be out/inout to be passed as an out/inout argument") else
            Replace
              (match g with
                | NotGhost -> codeLemma e
                | Ghost ->
                    let getType t = match t with Some t -> t | None -> err ((err_id x) + " must have type annotation") in
                    let es = if inParam then EVar (Reserved "old_s") else env.state in
                    vaEvalOp "op" (getType t) es e)
        | StateInfo (prefix, es, t) ->
          (
            match (g, asOperand) with
            | (Ghost, _) -> Replace (rewrite_state_info env x prefix es)
            | (NotGhost, Some xo) -> Replace (EOp (StateOp (x, xo + "_" + prefix, t), es))
            | (NotGhost, None) -> err "this expression can only be passed to a ghost parameter or operand parameter"
          )
      )
    | (NotGhost, ELoc _) -> Unchanged
    | (NotGhost, EOp (Uop UConst, [ec])) -> Replace (constOp (rewrite_vars_exp env ec))
    | (NotGhost, EInt _) -> Replace (constOp e)
    | (NotGhost, EApply (xa, args)) when (asOperand <> None && Map.containsKey (operandProc xa io) env.procs) ->
      (
        let p =
          match io with
          | In | InOut ->
            let xa_in = operandProc xa In in
            let p_in = Map.find xa_in env.procs in
            let p_in = {p_in with prets = []} in
            p_in
          | Out ->
            let xa_out = operandProc xa Out in
            let p_out = Map.find xa_out env.procs in
            let p_out = {p_out with pargs = List.take (List.length p_out.pargs - 1) p_out.pargs} in
            p_out
          in
        let (lhss, es) = rewrite_vars_args env p [] args in
        let xa_f = Reserved ("code_" + (string_of_id xa)) in
        Replace (EApply (xa_f, es))
      )
(*
    | (NotGhost, EOp (Subscript, [ea; ei])) ->
      (
        // Turn
        //   m[eax + 4]
        // into
        //   va_mem_lemma_HeapPublic(m, var_eax(), va_const_operand(4))
        match (skip_loc ea, skip_loc ei) with
        | (EVar xa, EOp (Bop BAdd, [eb; eo])) ->
            let ta = match Map.tryFind xa env.ids with Some (GhostLocal (Some (TName (Id ta)))) -> ta | _ -> err ("memory operand " + (err_id xa) + " must be a local ghost variable declared with a simple, named type") in
            let eb = rewrite_vars_arg NotGhost None In env eb in
            let eo = rewrite_vars_arg NotGhost None In env eo in
            let eCode = vaApp ("mem_code_" + ta) [eb; eo] in
            let eLemma = vaApp ("mem_lemma_" + ta) [ea; eb; eo] in
            Replace (EOp (CodeLemmaOp, [eCode; eLemma]))
        | _ -> err ("memory operand must have the form mem[base + offset] where mem is a variable")
      )
*)
    | (NotGhost, _) ->
        err "unsupported expression (if the expression is intended as a const operand, try wrapping it in 'const(...)'; if the expression is intended as a non-const operand, try declaring operand procedures)"
        // Replace (codeLemma e)
    | (Ghost, EOp (Uop UToOperand, [e])) -> Replace (rewrite_vars_arg NotGhost None io env e)
// TODO: this is a real error message, it should be uncommented
//    | (_, EApply (x, _)) when Map.containsKey x env.procs ->
//        err ("cannot call a procedure from inside an expression or variable declaration")
    | (Ghost, _) -> Unchanged
    in
  try
    env_map_exp fe env e
  with err -> (match locs_of_exp e with [] -> raise err | loc::_ -> raise (LocErr (loc, err)))
and rewrite_vars_exp (env:env) (e:exp):exp =
  rewrite_vars_arg Ghost None In env e
and rewrite_vars_args (env:env) (p:proc_decl) (rets:lhs list) (args:exp list):(lhs list * exp list) =
  let (mrets, margs) = match_proc_args env p rets args in
  let rewrite_arg (pp, ea) =
    match pp with
    | (x, t, XOperand xo, io, _) -> [rewrite_vars_arg NotGhost (Some xo) io env ea]
    | (x, t, XInline, io, _) -> [(rewrite_vars_arg Ghost None io env ea)]
    | (x, t, XAlias _, io, _) ->
        let _ = rewrite_vars_arg NotGhost None io env ea in // check argument validity
        [] // drop argument
    | (x, t, XGhost, In, []) -> [EOp (Uop UGhostOnly, [rewrite_vars_exp env ea])]
    | (x, t, XGhost, _, []) -> err ("out/inout ghost parameters are not supported")
    | (x, _, _, _, _) -> err ("unexpected argument for parameter " + (err_id x) + " in call to " + (err_id p.pname))
    in
  let rewrite_ret (pp, ((xlhs, _) as lhs)) =
    match pp with
    | (x, t, XOperand xo, _, _) -> ([], [rewrite_vars_arg NotGhost (Some xo) Out env (EVar xlhs)])
    | (x, t, XAlias _, _, _) ->
        let _ = rewrite_vars_arg NotGhost None Out env (EVar xlhs) in // check argument validity
        ([], []) // drop argument
    | (x, t, XGhost, _, []) -> ([lhs], [])
    | (x, _, _, _, _) -> err ("unexpected variable for return value " + (err_id x) + " in call to " + (err_id p.pname))
    in
  let args = List.concat (List.map rewrite_arg margs) in
  let (retsR, retsA) = List.unzip (List.map rewrite_ret mrets) in
  (List.concat retsR, (List.concat retsA) @ args)

// Turn
//   ecx < 10
// into
//   va_cmp_lt(var_ecx(), va_const_operand(10))
let rewrite_cond_exp (env:env) (e:exp):exp =
  let r = rewrite_vars_arg NotGhost (Some "cmp") In env in
  match skip_loc e with
  | (EOp (op, es)) ->
    (
      match (op, es) with
      | (Bop BEq, [e1; e2]) -> vaApp "cmp_eq" [r e1; r e2]
      | (Bop BNe, [e1; e2]) -> vaApp "cmp_ne" [r e1; r e2]
      | (Bop BLe, [e1; e2]) -> vaApp "cmp_le" [r e1; r e2]
      | (Bop BGe, [e1; e2]) -> vaApp "cmp_ge" [r e1; r e2]
      | (Bop BLt, [e1; e2]) -> vaApp "cmp_lt" [r e1; r e2]
      | (Bop BGt, [e1; e2]) -> vaApp "cmp_gt" [r e1; r e2]
      | _ -> err ("conditional expression must be a comparison operation")
    )
  | _ -> err ("conditional expression must be a comparison operation")

let rec rewrite_vars_assign (env:env) (lhss:lhs list) (e:exp):(lhs list * exp) =
  match (lhss, e) with
  | (_, ELoc (loc, e)) ->
      try let (lhss, e) = rewrite_vars_assign env lhss e in (lhss, ELoc (loc, e))
      with err -> raise (LocErr (loc, err))
  | (_, EApply(x, es)) ->
    (
      match Map.tryFind x env.procs with
      | None -> (lhss, rewrite_vars_exp env e)
      | Some p ->
          let (lhss, args) = rewrite_vars_args env p lhss es in
          (lhss, EApply(x, args))
    )
  | _ -> (lhss, rewrite_vars_exp env e)

let rewrite_vars_stmt (env:env) (s:stmt):(env * stmt list) =
  let rec fs (env:env) (s:stmt):(env * stmt list) map_modify =
    match s with
    | SAssign (lhss, e) ->
        let lhss = List.map (fun xd -> match xd with (Reserved "this", None) -> (Reserved "s", None) | _ -> xd) lhss in
        let (lhss, e) = rewrite_vars_assign env lhss e in
        Replace (env, [SAssign (lhss, e)])
    | SIfElse (SmPlain, e, b1, b2) ->
        let b1 = env_map_stmts rewrite_vars_exp fs env b1 in
        let b2 = env_map_stmts rewrite_vars_exp fs env b2 in
        Replace (env, [SIfElse (SmPlain, rewrite_cond_exp env e, b1, b2)])
    | SWhile (e, invs, ed, b) ->
        let invs = List_mapSnd (rewrite_vars_exp env) invs in
        let ed = mapSnd (List.map (rewrite_vars_exp env)) ed in
        let b = env_map_stmts rewrite_vars_exp fs env b in
        Replace (env, [SWhile (rewrite_cond_exp env e, invs, ed, b)])
    | _ -> Unchanged
    in
  env_map_stmt rewrite_vars_exp fs env s
let rewrite_vars_stmts (env:env) (ss:stmt list):stmt list =
  List.concat (snd (List_mapFoldFlip rewrite_vars_stmt env ss))

let rewrite_vars_spec (envIn:env) (envOut:env) (s:spec):spec =
  match s with
  | Requires e -> Requires (rewrite_vars_exp envIn e)
  | Ensures e -> Ensures (rewrite_vars_exp envOut e)
  | Modifies (m, e) -> Modifies (m, rewrite_vars_exp envOut e)

///////////////////////////////////////////////////////////////////////////////
// Add extra asserts for p's ensures clauses and for called procedures' requires clauses,
// to produce better error messages.

let rec is_while_proc (ss:stmt list):bool =
  match ss with
  | [s] ->
    (
      match skip_loc_stmt s with
      | SWhile (e, invs, ed, _) -> true
      | _ -> false
    )
  | s::ss when (match skip_locs_stmt s with [SVar (_, _, XGhost, _, _)] | [SAssign ([(_, None)], EVar _)] -> true | _ -> false) -> is_while_proc ss
  | _ -> false

let add_req_ens_asserts (env:env) (loc:loc) (p:proc_decl) (ss:stmt list):stmt list =
  if is_while_proc ss then ss else
  let hideResults ss = // wrap assertions in "forall ensures true" for better performance
    SForall ([], [], EBool true, EBool true, ss)
    in
  let rec fs (loc:loc) (s:stmt):stmt list map_modify =
    let reqAssert (f:exp -> exp) (loc, spec) =
      match spec with
      | Requires (EOp (Uop UUnrefinedSpec, _)) -> []
      | Requires e -> [SLoc (loc, SAssert (NotInv, f e))]
      | _ -> []
      in
    let rec assign e =
      match e with
      | ELoc (loc, e) -> try assign e with err -> raise (LocErr (loc, err))
      | EApply (x, es) when Map.containsKey x env.raw_procs ->
        let pCall = Map.find x env.raw_procs in
        if List.length es = List.length pCall.pargs then
          (* Generate one assertion for each precondition of the procedure pCall that we're calling.
             Also generate "assert true" to mark the location of the call itself.
             Wrap the whole thing in "forall ensures true" to improve verification performance.
          forall 
            ensures true
          {
            ghost var va_tmp_dst := va_x90_eax;
            ghost var va_tmp_ptr := va_old_esi;
            ghost var va_tmp_offset := 60;
            assert true;
            assert inMem(va_tmp_ptr + va_tmp_offset, va_x99_mem);
          }
          *)
          let xs = List.map (fun (x, _, _, _, _) -> x) pCall.pargs in
          let rename x = Reserved ("tmp_" + string_of_id x) in
          let xSubst = Map.ofList (List.map (fun x -> (x, EVar (rename x))) xs) in
          let xDecl x e = SVar (rename x, None, XGhost, [], Some e) in
          let xDecls = List.map2 xDecl xs es in
          let f e =
//            let f2 e x ex = EBind (BindLet, [ex], [(rename x, None)], [], e) in
//            List.fold2 f2 (subst_reserved_exp xSubst e) xs es
            subst_reserved_exp xSubst e
            in
          let reqAsserts = (List.collect (reqAssert f) pCall.pspecs) in
          let reqMarker = SLoc (loc, SAssert (NotInv, EBool true)) in
          Replace ([hideResults (xDecls @ (reqMarker::reqAsserts)); s])
        else Unchanged
      | _ -> Unchanged
      in
    match s with
    | SLoc (loc, s) -> fs loc s
    | SAssign (_, e) -> assign e
    | _ -> Unchanged
    in
  let ss = map_stmts (fun e -> e) (fs loc) ss in
  let ensStmt (loc, s) =
    (* Generate one assertion for each postcondition of the current procedure p.
       Wrap each assertion in a "forall ensures true" to improve verification performance.
    forall 
      ensures true
    {
      assert va_old_esi + 64 <= va_old_edi || va_old_edi + 64 <= va_old_esi;
    }
    *)
    match s with
    | Ensures (EOp (Uop UUnrefinedSpec, _)) -> []
    | Ensures e -> [hideResults [SLoc (loc, SAssert (NotInv, e))]]
    | _ -> []
    in
  let ensStmts = List.collect ensStmt p.pspecs in
  ss @ ensStmts

///////////////////////////////////////////////////////////////////////////////

let transform_decl (env:env) (loc:loc) (d:decl):(env * decl * decl) =
  match d with
  | DVar (x, t, XAlias (AliasThread, e), _) ->
      let env = {env with ids = Map.add x (ThreadLocal {local_in_param = false; local_exp = e; local_typ = Some t}) env.ids} in
      (env, d, d)
  | DVar (x, t, XState e, _) ->
    (
      match skip_loc e with
      | EApply (Id id, es) ->
          let env = {env with ids = Map.add x (StateInfo (id, es, t)) env.ids} in
          (env, d, d)
      | _ -> err ("declaration of state member " + (err_id x) + " must provide an expression of the form f(...args...)")
    )
  | DProc p ->
    (
      let isRefined = attrs_get_bool (Id "refined") false p.pattrs in
      let isFrame = attrs_get_bool (Id "frame") true p.pattrs in
      let isRecursive = attrs_get_bool (Id "recursive") false p.pattrs in
      let isInstruction = List_mem_assoc (Id "instruction") p.pattrs in
      let ok = EVar (Id "ok") in
      let okSpecs = [(loc, Requires ok); (loc, Ensures ok); (loc, Modifies (true, ok))] in
      let pspecs = if isRefined || isFrame then okSpecs @ p.pspecs else p.pspecs in
      let addParam isRet ids (x, t, g, io, a) =
        match g with
        | (XAlias (AliasThread, e)) -> Map.add x (ThreadLocal {local_in_param = (io = In && (not isRet)); local_exp = e; local_typ = Some t}) ids
        | (XAlias (AliasLocal, e)) -> Map.add x (ProcLocal {local_in_param = (io = In && (not isRet)); local_exp = e; local_typ = Some t}) ids
        | XInline -> Map.add x InlineLocal ids
        | XOperand xo -> Map.add x (OperandLocal (io, xo, t)) ids
        | XPhysical | XState _ -> err ("variable must be declared ghost, operand, {:local ...}, or {:register ...} " + (err_id x))
        | XGhost -> Map.add x (GhostLocal (Some t)) ids
        in
      let mods = List.collect (fun (loc, s) -> match s with Modifies (modifies, e) -> [(loc, modifies, e)] | _ -> []) pspecs in
      let mod_id (loc, modifies, e) =
        let mod_err () = err "expression in modifies clause must be a variable declared as var{:state f(...)} x:t;"
        loc_apply loc e (fun e ->
          match e with
          | EVar x ->
            (
              match Map.tryFind x env.ids with
              | None -> err ("cannot find variable " + (err_id x))
              | Some (StateInfo _) -> (x, modifies)
              | Some _ -> mod_err ()
            )
          | _ -> mod_err ())
        in
      let envpIn = env in
      let envpIn = {envpIn with mods = Map.ofList (List.map mod_id mods)} in
      let envpIn = {envpIn with ids = List.fold (addParam false) env.ids p.pargs} in
      let envp = {envpIn with ids = List.fold (addParam true) envpIn.ids p.prets} in
      let envpIn = {envpIn with abstractOld = true} in
      let env = {env with raw_procs = Map.add p.pname p env.raw_procs}
      let specs = List_mapSnd (rewrite_vars_spec envpIn envp) pspecs in
      let envp = if isRecursive then {envp with procs = Map.add p.pname {p with pspecs = specs} envp.procs} else envp in
      let resolveBody =
        match p.pbody with
        | None -> None
        | Some ss -> let ss = resolve_overload_stmts envp ss in Some ss
        in
      let body =
        match p.pbody with
        | None -> None
        | Some ss ->
            let rec add_while_ok s =
              match s with
              | SWhile (e, invs, ed, b) ->
                  let invs = (loc, EVar (Id "ok"))::invs in
                  Replace [SWhile (e, invs, ed, map_stmts (fun e -> e) add_while_ok b)]
              | _ -> Unchanged
              in
            let ss = if isFrame then map_stmts (fun e -> e) add_while_ok ss else ss in
            let ss = if isRefined && not isInstruction then add_req_ens_asserts env loc p ss else ss in
            let ss = resolve_overload_stmts envp ss in
            //let ss = assume_updates_stmts envp p.pargs p.prets ss (List.map snd pspecs) in
            let ss = rewrite_vars_stmts envp ss in
            Some ss
        in
      (env, DProc {p with pbody = resolveBody}, DProc {p with pbody = body; pspecs = specs})
    )
  | _ -> (env, d, d)


