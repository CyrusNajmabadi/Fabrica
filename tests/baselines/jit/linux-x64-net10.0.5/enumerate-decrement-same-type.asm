; Assembly listing for method Program:<<Main>$>g__EnumerateDecrement|0_N(byref,byref,byref) (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Unix
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rax
 
G_M000_IG02:                ;; offset=0x0001
       call     [TreeChildEnumerator:EnumerateChildren[Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]](byref,byref):this]
       nop      
 
G_M000_IG03:                ;; offset=0x0008
       add      rsp, 8
       ret      
 
; Total bytes of code 13

