using FujinTerm.Game;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace FujinTerm.Tests;

/// <summary>
/// IL-scans the compiled FujinTerm assembly for every field marked with
/// <see cref="Services.OwnerAttribute"/> and asserts that no method
/// outside the declaring type or the declared owner type writes the
/// field via <c>stfld</c>. If a future PR accidentally adds a second
/// writer (e.g. by exposing internal accessors or via reflection-style
/// patterns), this test fails and the violation surfaces in CI.
/// </summary>
/// <remarks>
/// Why <c>stfld</c> in particular: the C# compiler only emits <c>stfld</c>
/// against a private field from inside its declaring type, so the test's
/// real value is a guard against future tricks (internal helpers in the
/// same assembly, code-generated mutators) that would silently bypass
/// the property setter. The property setter itself <i>does</i> emit
/// <c>stfld</c> — but it lives in the declaring type, which we allow.
/// </remarks>
public sealed class SingleWriterInvariantTests
{
    [Fact]
    public void NoForeignClassWritesOwnedFields()
    {
        // Anchor on a known FujinTerm type to find the assembly file.
        string assemblyPath = typeof(PlayerState).Assembly.Location;
        using AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(assemblyPath);

        // Map field FullName → declared owner type FullName, plus declaring
        // type FullName for the "field's own class is always allowed"
        // exception. Owner names come from the OwnerAttribute argument.
        Dictionary<string, (string Declaring, string Owner)> ownedFields = new();
        foreach (TypeDefinition type in asm.MainModule.GetTypes())
        {
            foreach (FieldDefinition field in type.Fields)
            {
                CustomAttribute? attr = field.CustomAttributes
                    .FirstOrDefault(a => a.AttributeType.FullName == "FujinTerm.Services.OwnerAttribute");
                if (attr is null) continue;
                if (attr.ConstructorArguments.Count == 0) continue;
                if (attr.ConstructorArguments[0].Value is not TypeReference ownerRef) continue;

                ownedFields[field.FullName] = (type.FullName, ownerRef.FullName);
            }
        }

        Assert.NotEmpty(ownedFields);   // sanity — at least PlayerState's fields are marked.

        List<string> violations = new();
        foreach (TypeDefinition type in asm.MainModule.GetTypes())
        {
            foreach (MethodDefinition method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (Instruction instr in method.Body.Instructions)
                {
                    if (instr.OpCode != OpCodes.Stfld) continue;
                    if (instr.Operand is not FieldReference fref) continue;
                    string fieldName = fref.FullName;
                    if (!ownedFields.TryGetValue(fieldName, out var rule)) continue;

                    string writingType = method.DeclaringType.FullName;
                    if (writingType == rule.Declaring || writingType == rule.Owner) continue;

                    violations.Add($"{writingType}::{method.Name} writes {fieldName} (owner: {rule.Owner})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Single-writer invariant violations:\n  " + string.Join("\n  ", violations));
    }
}
