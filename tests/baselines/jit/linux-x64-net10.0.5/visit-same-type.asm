; Assembly listing for method Program:<<Main>$>g__VisitSameType|0_N(byref,Fabrica.Core.Memory.Handle`1[TreeNode]) (FullOpts)
; Emitting BLENDED_CODE for generic X64 on Unix
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rax
 
G_M000_IG02:                ;; offset=0x0001
       call     [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]:Visit[TreeNode](Fabrica.Core.Memory.Handle`1[TreeNode]):this]
       nop      
 
G_M000_IG03:                ;; offset=0x0008
       add      rsp, 8
       ret      
 
; Total bytes of code 13

