; Assembly listing for method Program:<<Main>$>g__EnumerateMixedDecrement|0_N(byref,byref,byref) (FullOpts)
; Emitting BLENDED_CODE for generic X64 on Unix
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rax
 
G_M000_IG02:                ;; offset=0x0001
       mov      rax, <addr>
       mov      byte  ptr [rax], 1
       call     [MixedChildEnumerator:EnumerateChildren[MixedDecrementVisitor](byref,byref):this]
       mov      rax, <addr>
       mov      byte  ptr [rax], 1
 
G_M000_IG03:                ;; offset=0x0021
       add      rsp, 8
       ret      
 
; Total bytes of code 38
