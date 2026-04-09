; Assembly listing for method MixedChildEnumerator:EnumerateChildren[MixedDecrementVisitor](byref,byref):this (FullOpts)
; Emitting BLENDED_CODE for generic X64 on Unix
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
       mov      esi, dword ptr [rbx]
       mov      rdi, r15
       call     [MixedDecrementVisitor:Visit[MixedNode](Fabrica.Core.Memory.Handle`1[MixedNode]):this]
       mov      esi, dword ptr [rbx+0x04]
       mov      rdi, r15
 
G_M000_IG03:                ;; offset=0x0020
       pop      rbx
       pop      r15
       pop      rbp
       tail.jmp [MixedDecrementVisitor:Visit[OtherNode](Fabrica.Core.Memory.Handle`1[OtherNode]):this]
 
; Total bytes of code 44
