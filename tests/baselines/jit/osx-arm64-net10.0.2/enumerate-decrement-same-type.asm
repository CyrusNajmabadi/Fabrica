; Assembly listing for method Program:<<Main>$>g__EnumerateDecrement|0_N(byref,byref,byref) (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Apple
; FullOpts code
; optimized code
; fp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 10 single block inlinees; 3 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
            stp     fp, lr, [sp, #-0x10]!
            mov     fp, sp
 
G_M000_IG02:                ;; offset=0x0008
            ldr     w0, [x1]
            tbnz    w0, #31, G_M000_IG04
 
G_M000_IG03:                ;; offset=0x0010
            ldr     x3, [x2]
            ldp     w4, w5, [x3, #0x14]
            asr     w4, w0, w4
            and     w0, w0, w5
            ldr     x3, [x3, #0x08]
            ldrsb   wzr, [x3], #0x10
            ldr     x3, [x3, w4, SXTW #3]
            ldrsb   wzr, [x3], #0x10
            sbfiz   x0, x0, #2, #32
            add     x0, x3, x0
            ldr     w3, [x0]
            sub     w3, w3, #1
            str     w3, [x0]
 
G_M000_IG04:                ;; offset=0x0044
            ldr     w0, [x1, #0x04]
            tbnz    w0, #31, G_M000_IG06
 
G_M000_IG05:                ;; offset=0x004C
            ldr     x1, [x2]
            ldp     w2, w3, [x1, #0x14]
            asr     w2, w0, w2
            and     w0, w0, w3
            ldr     x1, [x1, #0x08]
            ldrsb   wzr, [x1], #0x10
            ldr     x1, [x1, w2, SXTW #3]
            ldrsb   wzr, [x1], #0x10
            sbfiz   x0, x0, #2, #32
            add     x0, x1, x0
            ldr     w1, [x0]
            sub     w1, w1, #1
            str     w1, [x0]
 
G_M000_IG06:                ;; offset=0x0080
            ldp     fp, lr, [sp], #0x10
            ret     lr
 
; Total bytes of code 136

