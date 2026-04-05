; Assembly listing for method MixedChildEnumerator:EnumerateChildren[Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[MixedNode,MixedHandler]](byref,byref):this (FullOpts)
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
       sub      rsp, 16
       lea      rbp, [rsp+0x20]
       xor      eax, eax
       mov      qword ptr [rbp-0x20], rax
       mov      rbx, rsi
       mov      r15, rdx
 
G_M000_IG02:                ;; offset=0x0019
       mov      rdi, rbx
       call     [MixedNode:get_SameChild():Fabrica.Core.Memory.Handle`1[MixedNode]:this]
       mov      dword ptr [rbp-0x18], eax
       lea      rdi, [rbp-0x18]
       call     [Fabrica.Core.Memory.Handle`1[MixedNode]:get_IsValid():bool:this]
       test     eax, eax
       je       SHORT G_M000_IG04
 
G_M000_IG03:                ;; offset=0x0033
       mov      rdi, rbx
       call     [MixedNode:get_SameChild():Fabrica.Core.Memory.Handle`1[MixedNode]:this]
       mov      esi, eax
       mov      rdi, r15
       call     [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[MixedNode,MixedHandler]:Visit[MixedNode](Fabrica.Core.Memory.Handle`1[MixedNode]):this]
 
G_M000_IG04:                ;; offset=0x0047
       mov      rdi, rbx
       call     [MixedNode:get_CrossChild():Fabrica.Core.Memory.Handle`1[OtherNode]:this]
       mov      dword ptr [rbp-0x20], eax
       lea      rdi, [rbp-0x20]
       call     [Fabrica.Core.Memory.Handle`1[OtherNode]:get_IsValid():bool:this]
       test     eax, eax
       je       SHORT G_M000_IG06
 
G_M000_IG05:                ;; offset=0x0061
       mov      rdi, rbx
       call     [MixedNode:get_CrossChild():Fabrica.Core.Memory.Handle`1[OtherNode]:this]
       mov      esi, eax
       mov      rdi, r15
       call     [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[MixedNode,MixedHandler]:Visit[OtherNode](Fabrica.Core.Memory.Handle`1[OtherNode]):this]
 
G_M000_IG06:                ;; offset=0x0075
       nop      
 
G_M000_IG07:                ;; offset=0x0076
       add      rsp, 16
       pop      rbx
       pop      r15
       pop      rbp
       ret      
 
; Total bytes of code 127

