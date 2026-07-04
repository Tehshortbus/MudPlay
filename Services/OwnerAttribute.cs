namespace FujinTerm.Services;

// Marks an [ObservableProperty]-backed field as having exactly one writer
// outside its declaring type. The FujinTerm.Tests single-writer invariant
// test scans the compiled assembly's IL and fails CI if any class other than
// Owner or the field's declaring type writes the field via stfld.
//
// Example:
//   public sealed partial class PlayerState : ObservableObject
//   {
//       [ObservableProperty]
//       [field: Owner(typeof(PromptParser))]
//       private int _hp;
//   }
//
// The attribute lives on the FIELD via [field: …] targeting because
// [ObservableProperty] generates the public property and the attribute needs
// to attach to the backing field the test scans.
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class OwnerAttribute : Attribute
{
    // The sole non-declaring-type class allowed to write this field.
    public Type Owner { get; }

    public OwnerAttribute(Type owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Owner = owner;
    }
}
