; Assembly listing for method Program:<<Main>$>g__VisitDifferentType|0_N(byref,byref) (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Apple
; FullOpts code
; optimized code
; fp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 0 single block inlinees; 1 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
            stp     fp, lr, [sp, #-0x10]!
            mov     fp, sp
 
G_M000_IG02:                ;; offset=0x0008
            ldp     fp, lr, [sp], #0x10
            ret     lr
 
; Total bytes of code 16

