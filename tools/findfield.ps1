param([string]$AsmPath, [string]$Field)

Add-Type -Path "d:\trae\newcode\xukongweiduta\.ref\Mono.Cecil.dll"
$def = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AsmPath)
foreach ($t in $def.MainModule.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($ins in $m.Body.Instructions) {
            $code = $ins.OpCode.Code.ToString()
            if ($code -ne "Stfld" -and $code -ne "Ldfld") { continue }
            $op = $ins.Operand
            if ($op -is [Mono.Cecil.FieldReference] -and $op.Name -eq $Field) {
                "{0,7}  {1}::{2}" -f $code, $t.FullName, $m.Name
            }
        }
    }
}
