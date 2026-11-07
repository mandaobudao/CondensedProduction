using Mono.Cecil;
using Mono.Cecil.Cil;

public class Callers
{
    public static void Main(string[] args)
    {
        var asm = AssemblyDefinition.ReadAssembly(args[0]);
        string target = args[1];
        foreach (var t in asm.MainModule.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if ((ins.OpCode.Code == Code.Call || ins.OpCode.Code == Code.Callvirt)
                        && ins.Operand is MethodReference mr && mr.Name == target)
                    {
                        System.Console.WriteLine(t.FullName + "::" + m.Name + "  ->  " + mr.ToString());
                    }
                }
            }
        }
    }
}
