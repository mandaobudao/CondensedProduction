param([string]$AsmPath, [string]$Target)

Add-Type -Path "d:\trae\newcode\xukongweiduta\.ref\Mono.Cecil.dll"
$def = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AsmPath)
$n = 0
foreach ($t in $def.MainModule.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $n++
        foreach ($ins in $m.Body.Instructions) {
            $code = $ins.OpCode.Code.ToString()
            if ($code -ne "Call" -and $code -ne "Callvirt") { continue }
            $op = $ins.Operand
            if ($op -is [Mono.Cecil.MethodReference] -and $op.Name -eq $Target) {
                "{0}::{1}  ->  {2}" -f $t.FullName, $m.Name, $op.ToString()
            }
        }
    }
}
"scanned $n methods"
