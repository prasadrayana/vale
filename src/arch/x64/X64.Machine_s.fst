module X64.Machine_s

unfold let nat32_max = 0x100000000
unfold let nat64_max = 0x10000000000000000
unfold let nat128_max = 0x100000000000000000000000000000000

// Sanity check our constants
let _ = assert_norm (pow2 32 = nat32_max)
let _ = assert_norm (pow2 64 = nat64_max)
let _ = assert_norm (pow2 128 = nat128_max)

type nat64 = x:nat{x < nat64_max}
assume val int_to_nat64 : i:int -> n:nat64{0 <= i && i < nat64_max ==> i == n}
type nat128 = x:nat{x < nat128_max}

type reg =
  | Rax
  | Rbx
  | Rcx
  | Rdx
  | Rsi
  | Rdi
  | Rbp
  | Rsp
  | R8
  | R9
  | R10
  | R11
  | R12
  | R13
  | R14
  | R15

let string_of_reg (r:reg) =
  match r with
  | Rax -> "Rax"
  | Rbx -> "Rbx"
  | Rcx -> "Rcx"
  | Rdx -> "Rdx"
  | Rsi -> "Rsi"
  | Rdi -> "Rdi"
  | Rbp -> "Rbp"
  | Rsp -> "Rsp"
  | R8  -> "R8"
  | R9  -> "R9"
  | R10 -> "R10"
  | R11 -> "R11"
  | R12 -> "R12"
  | R13 -> "R13"
  | R14 -> "R14"
  | R15 -> "R15"

type maddr =
  | MConst: n:int -> maddr
  | MReg: r:reg -> offset:int -> maddr
  | MIndex: base:reg -> scale:int -> index:reg -> offset:int -> maddr

let string_of_maddr (addr:maddr) : string =
  match addr with
  | MConst n -> string_of_int n
  // | MReg r offset -> strcat (string_of_int offset) (strcat ("(")
  // 				       (strcat (string_of_reg r) (")")))
  | MReg r ofs -> string_of_reg r
  | _ -> "todo Mindex"

type operand =
  | OConst: n:int -> operand
  | OReg: r:reg -> operand
  | OMem: m:maddr -> operand

type precode (t_ins:Type0) (t_ocmp:Type0) =
  | Ins: ins:t_ins -> precode t_ins t_ocmp
  | Block: block:list (precode t_ins t_ocmp) -> precode t_ins t_ocmp
  | IfElse: ifCond:t_ocmp -> ifTrue:precode t_ins t_ocmp -> ifFalse:precode t_ins t_ocmp -> precode t_ins t_ocmp
  | While: whileCond:t_ocmp -> whileBody:precode t_ins t_ocmp -> inv:operand -> precode t_ins t_ocmp

let valid_dst (o:operand) : bool =
  not(OConst? o || (OReg? o && Rsp? (OReg?.r o)))

type dst_op = o:operand{valid_dst o}

