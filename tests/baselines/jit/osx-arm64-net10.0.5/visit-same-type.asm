; Assembly listing for method Program:<<Main>$>g__VisitSameType|0_N(byref,Fabrica.Core.Memory.Handle`1[TreeNode]) (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Apple
; FullOpts code
; optimized code
; fp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 3 single block inlinees; 1 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
            stp     fp, lr, [sp, #-0x10]!
            mov     fp, sp
 
G_M000_IG02:                ;; offset=0x0008
            ldr     x0, [x0]
            ldr     x0, [x0, #0x08]
            ldp     w2, w3, [x0, #0x14]
            asr     w2, w1, w2
            and     w1, w1, w3
            ldr     x0, [x0, #0x08]
            ldrsb   wzr, [x0], #0x10
            ldr     x0, [x0, w2, SXTW #3]
            ldrsb   wzr, [x0], #0x10
            sbfiz   x1, x1, #2, #32
            add     x0, x0, x1
            ldr     w1, [x0]
            sub     w1, w1, #1
            str     w1, [x0]
 
G_M000_IG03:                ;; offset=0x0040
            ldp     fp, lr, [sp], #0x10
            ret     lr
 
; Total bytes of code 72

