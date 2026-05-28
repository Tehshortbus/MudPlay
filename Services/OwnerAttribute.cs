namespace FujinTerm.Services;

/// <summary>
/// Marks an <see cref="CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute"/>-backed
/// field as having exactly one writer outside its declaring type. The
/// FujinTerm.Tests single-writer invariant test scans the compiled
/// assembly's IL and fails CI if any class other than
/// <see cref="Owner"/> or the field's declaring type writes the field
/// via <c>stfld</c>.
/// </summary>
/// <remarks>
/// Example:
/// <code>
/// public sealed partial class PlayerState : ObservableObject
/// {
///     [ObservableProperty]
///     [field: Owner(typeof(PromptParser))]
///     private int _hp;
/// }
/// </code>
/// The attribute lives on the FIELD via <c>[field: …]</c> targeting
/// because <c>[ObservableProperty]</c> generates the public property and
/// the attribute needs to attach to the backing field the test scans.
/// </remarks>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class OwnerAttribute : Attribute
{
    /// <summary>The sole non-declaring-type class allowed to write this field.</summary>
    public Type Owner { get; }

    public OwnerAttribute(Type owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        Owner = owner;
    }
}
