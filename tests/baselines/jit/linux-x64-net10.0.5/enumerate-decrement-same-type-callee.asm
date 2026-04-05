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
       sub      rsp, 16
       lea      rbp, [rsp+0x20]
       mov      rbx, rsi
       mov      r15, rdx
 
G_M000_IG02:                ;; offset=0x0013
       mov      rdi, rbx
       call     [TreeNode:get_Left():Fabrica.Core.Memory.Handle`1[TreeNode]:this]
       mov      dword ptr [rbp-0x18], eax
       lea      rdi, [rbp-0x18]
       call     [Fabrica.Core.Memory.Handle`1[TreeNode]:get_IsValid():bool:this]
       test     eax, eax
       je       SHORT G_M000_IG04
 
G_M000_IG03:                ;; offset=0x002D
       mov      rdi, rbx
       call     [TreeNode:get_Left():Fabrica.Core.Memory.Handle`1[TreeNode]:this]
       mov      esi, eax
       mov      rdi, r15
       call     [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]:Visit[TreeNode](Fabrica.Core.Memory.Handle`1[TreeNode]):this]
 
G_M000_IG04:                ;; offset=0x0041
       mov      rdi, rbx
       call     [TreeNode:get_Right():Fabrica.Core.Memory.Handle`1[TreeNode]:this]
       mov      dword ptr [rbp-0x18], eax
       lea      rdi, [rbp-0x18]
       call     [Fabrica.Core.Memory.Handle`1[TreeNode]:get_IsValid():bool:this]
       test     eax, eax
       je       SHORT G_M000_IG06
 
G_M000_IG05:                ;; offset=0x005B
       mov      rdi, rbx
       call     [TreeNode:get_Right():Fabrica.Core.Memory.Handle`1[TreeNode]:this]
       mov      esi, eax
       mov      rdi, r15
       call     [Fabrica.Core.Memory.RefCountTable`1+DecrementNodeRefCountVisitor`1[TreeNode,TreeHandler]:Visit[TreeNode](Fabrica.Core.Memory.Handle`1[TreeNode]):this]
 
G_M000_IG06:                ;; offset=0x006F
       nop      
 
G_M000_IG07:                ;; offset=0x0070
       add      rsp, 16
       pop      rbx
       pop      r15
       pop      rbp
       ret      
 
; Total bytes of code 121

