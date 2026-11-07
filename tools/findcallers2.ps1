param([string]$AsmPath, [string]$Target, [int]$Before = 2)

Add-Type -Path "d:\trae\newcode\xukongweiduta\.ref\Mono.Cecil.dll"
$def = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AsmPath)
foreach ($t in $def.MainModule.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = $m.Body.Instructions
        for ($i = 0; $i -lt $ins.Count; $i++) {
            $code = $ins[$i].OpCode.Code.ToString()
            if ($code -ne "Call" -and $code -ne "Callvirt") { continue }
            $op = $ins[$i].Operand
            if ($op -is [Mono.Cecil.MethodReference] -and $op.Name -eq $Target) {
                "--- {0}::{1}" -f $t.FullName, $m.Name
                $start = [Math]::Max(0, $i - $Before)
                for ($k = $start; $k -lt $i; $k++) { "    " + $ins[$k].ToString() }
                "  >> " + $ins[$i].ToString()
            }
        }
    }
}
