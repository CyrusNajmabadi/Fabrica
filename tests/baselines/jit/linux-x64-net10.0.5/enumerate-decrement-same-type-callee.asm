; Assembly listing for method TreeChildEnumerator:EnumerateChildren[Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]](byref,byref):this (FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Unix
; FullOpts code
; optimized code
; rbp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
push     rbp
push     r15
push     rbx
lea      rbp, [rsp+0x10]
mov      rbx, rsi
mov      r15, rdx

G_M000_IG02:                ;; offset=0x000F
cmp      byte  ptr [rbx], bl
mov      rdi, rbx
call     [Fabrica.Core.Memory.Handle`1[TreeNode]:get_IsValid():bool:this]
test     eax, eax
je       SHORT G_M000_IG04

G_M000_IG03:                ;; offset=0x001E
mov      esi, dword ptr [rbx]
mov      rdi, r15
call     [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]:Visit[TreeNode](Fabrica.Core.Memory.Handle`1[TreeNode]):this]

G_M000_IG04:                ;; offset=0x0029
lea      rdi, bword ptr [rbx+0x04]
call     [Fabrica.Core.Memory.Handle`1[TreeNode]:get_IsValid():bool:this]
test     eax, eax
jne      SHORT G_M000_IG06

G_M000_IG05:                ;; offset=0x0037
pop      rbx
pop      r15
pop      rbp
ret      

G_M000_IG06:                ;; offset=0x003C
mov      esi, dword ptr [rbx+0x04]
mov      rdi, r15

G_M000_IG07:                ;; offset=0x0042
pop      rbx
pop      r15
pop      rbp
tail.jmp [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]:Visit[TreeNode](Fabrica.Core.Memory.Handle`1[TreeNode]):this]

; Total bytes of code 76

