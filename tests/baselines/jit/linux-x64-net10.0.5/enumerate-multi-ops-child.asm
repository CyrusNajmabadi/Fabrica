; Assembly listing for method Program:<<Main>$>g__EnumerateMultiOpsChild|0_N(byref,byref,byref) (FullOpts)
; Emitting BLENDED_CODE for generic X64 on Unix
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rax
 
G_M000_IG02:                ;; offset=0x0001
       call     [Program:<<Main>$>g__ConstrainedEnumerate|0_N[MultiTypeOps,ChildNode,MultiTypeOps](byref,byref,byref)]
       nop      
 
G_M000_IG03:                ;; offset=0x0008
       add      rsp, 8
       ret      
 
; Total bytes of code 13

