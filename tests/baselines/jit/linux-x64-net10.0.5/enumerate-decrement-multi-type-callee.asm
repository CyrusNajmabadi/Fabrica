; Assembly listing for method ParentChildEnumerator:EnumerateChildren[ParentDecrementVisitor](byref,byref):this (FullOpts)
; Emitting BLENDED_CODE for generic X64 on Unix
; FullOpts code
; optimized code
; rsp based frame
; partially interruptible
; No PGO data

G_M000_IG01:                ;; offset=0x0000
       push     r15
       push     rbx
       push     rax
       mov      rbx, rsi
       mov      r15, rdx
 
G_M000_IG02:                ;; offset=0x000A
       mov      esi, dword ptr [rbx]
       mov      rdi, r15
       call     [ParentDecrementVisitor:Visit[ParentNode](Fabrica.Core.Memory.Handle`1[ParentNode]):this]
       mov      esi, dword ptr [rbx+0x04]
       mov      rdi, r15
 
G_M000_IG03:                ;; offset=0x001B
       add      rsp, 8
       pop      rbx
       pop      r15
       tail.jmp [ParentDecrementVisitor:Visit[ChildNode](Fabrica.Core.Memory.Handle`1[ChildNode]):this]
 
; Total bytes of code 40
 
