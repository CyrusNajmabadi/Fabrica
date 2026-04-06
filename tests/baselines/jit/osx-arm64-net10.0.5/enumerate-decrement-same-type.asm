; Assembly listing for method Program:<<Main>$>g__EnumerateDecrement|0_N(byref,byref,byref) (FullOpts)
; Emitting BLENDED_CODE for generic ARM64 on Apple
; FullOpts code
; optimized code
; fp based frame
; partially interruptible
; No PGO data
; 0 inlinees with PGO data; 10 single block inlinees; 7 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
            stp     fp, lr, [sp, #-0x70]!
            stp     x19, x20, [sp, #0x38]
            stp     x21, x22, [sp, #0x48]
            stp     x23, x24, [sp, #0x58]
            str     x25, [sp, #0x68]
            mov     fp, sp
            str     xzr, [fp, #0x28]
            str     xzr, [fp, #0x18]
            mov     x19, x1
            mov     x20, x2
 
G_M000_IG02:                ;; offset=0x0028
            ldr     w0, [x19]
            tbnz    w0, #31, G_M000_IG07
 
G_M000_IG03:                ;; offset=0x0030
            ldr     w21, [x19]
            ldr     x22, [x20]
 
G_M000_IG04:                ;; offset=0x0038
            ldp     x0, x1, [x20, #0x08]
            stp     x0, x1, [fp, #0x28]
 
G_M000_IG05:                ;; offset=0x0040
            ldr     x0, [x22, #0x08]
            ldp     w1, w2, [x0, #0x14]
            asr     w1, w21, w1
            and     w2, w21, w2
            ldr     x0, [x0, #0x08]
            ldrsb   wzr, [x0], #0x10
            ldr     x0, [x0, w1, SXTW #3]
            ldrsb   wzr, [x0], #0x10
            sbfiz   x1, x2, #2, #32
            add     x0, x0, x1
            ldr     w1, [x0]
            sub     w1, w1, #1
            str     w1, [x0]
            cbnz    w1, G_M000_IG07
            ldr     x0, [x22, #0x10]
            ldr     x23, [x0, #0x08]
            ldr     w24, [x23, #0x10]
            ldr     x25, [x23, #0x08]
            ldr     w0, [x25, #0x08]
            cmp     w0, w24
            bne     G_M000_IG06
            mov     x0, x23
            movz    x1, #<addr>
            movk    x1, #<addr> LSL #16
            movk    x1, #1 LSL #32
            ldr     x1, [x1]
            blr     x1
            mov     x25, x0
 
G_M000_IG06:                ;; offset=0x00B0
            ldrsb   wzr, [x25]
            add     x1, x25, #16
            str     w21, [x1, w24, SXTW #2]
            add     w1, w24, #1
            str     w1, [x23, #0x10]
            ldrb    w1, [x22, #0x18]
            cbnz    w1, G_M000_IG07
            ldp     x1, x2, [fp, #0x28]
            mov     x0, x22
            movz    x3, #<addr>
            movk    x3, #<addr> LSL #16
            movk    x3, #1 LSL #32
            ldr     x3, [x3]
            blr     x3
 
G_M000_IG07:                ;; offset=0x00E8
            ldr     w0, [x19, #0x04]
            tbnz    w0, #31, G_M000_IG12
 
G_M000_IG08:                ;; offset=0x00F0
            ldr     w19, [x19, #0x04]
            ldr     x21, [x20]
 
G_M000_IG09:                ;; offset=0x00F8
            ldp     x0, x1, [x20, #0x08]
            stp     x0, x1, [fp, #0x18]
 
G_M000_IG10:                ;; offset=0x0100
            ldr     x0, [x21, #0x08]
            ldp     w1, w2, [x0, #0x14]
            asr     w1, w19, w1
            and     w2, w19, w2
            ldr     x0, [x0, #0x08]
            ldrsb   wzr, [x0], #0x10
            ldr     x0, [x0, w1, SXTW #3]
            ldrsb   wzr, [x0], #0x10
            sbfiz   x1, x2, #2, #32
            add     x0, x0, x1
            ldr     w1, [x0]
            sub     w1, w1, #1
            str     w1, [x0]
            cbnz    w1, G_M000_IG12
            ldr     x0, [x21, #0x10]
            ldr     x20, [x0, #0x08]
            ldr     w22, [x20, #0x10]
            ldr     x23, [x20, #0x08]
            ldr     w0, [x23, #0x08]
            cmp     w0, w22
            bne     G_M000_IG11
            mov     x0, x20
            movz    x1, #<addr>
            movk    x1, #<addr> LSL #16
            movk    x1, #1 LSL #32
            ldr     x1, [x1]
            blr     x1
            mov     x23, x0
 
G_M000_IG11:                ;; offset=0x0170
            ldrsb   wzr, [x23]
            add     x1, x23, #16
            str     w19, [x1, w22, SXTW #2]
            add     w1, w22, #1
            str     w1, [x20, #0x10]
            ldrb    w1, [x21, #0x18]
            cbnz    w1, G_M000_IG12
            ldp     x1, x2, [fp, #0x18]
            mov     x0, x21
            movz    x3, #<addr>
            movk    x3, #<addr> LSL #16
            movk    x3, #1 LSL #32
            ldr     x3, [x3]
            blr     x3
 
G_M000_IG12:                ;; offset=0x01A8
            ldr     x25, [sp, #0x68]
            ldp     x23, x24, [sp, #0x58]
            ldp     x21, x22, [sp, #0x48]
            ldp     x19, x20, [sp, #0x38]
            ldp     fp, lr, [sp], #0x70
            ret     lr
 
; Total bytes of code 448

