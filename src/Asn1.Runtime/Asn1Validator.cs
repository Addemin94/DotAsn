using Asn1.Constraints;

namespace Asn1.Runtime;

/// <summary>A constraint or validity violation found by <see cref="Asn1Validator"/>.</summary>
/// <param name="Path">Component path.</param>
/// <param name="Message">Description.</param>
public sealed record Asn1ValidationError(string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}

/// <summary>Recursively validates values against their descriptors (constraints, repertoires, presence, time syntax).</summary>
public sealed class Asn1Validator
{
    private readonly List<Asn1ValidationError> _errors = [];
    private int _depth;

    /// <summary>Maximum recursion depth before validation stops with an error.</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>The current path.</summary>
    public Asn1Path Path { get; } = new();

    /// <summary>The errors found.</summary>
    public IReadOnlyList<Asn1ValidationError> Errors => _errors;

    /// <summary>Validates a value and returns all violations.</summary>
    public static IReadOnlyList<Asn1ValidationError> ValidateValue<T>(Asn1Type<T> type, T value)
    {
        var v = new Asn1Validator();
        v.Validate(type, value);
        return v.Errors;
    }

    /// <summary>Records an error at the current path (optionally below a component name).</summary>
    public void Report(string? component, string message)
    {
        var path = Path.ToString();
        if (component is not null)
        {
            path = path.Length == 0 ? component : path + "." + component;
        }

        _errors.Add(new Asn1ValidationError(path, message));
    }

    /// <summary>Validates a value of a type.</summary>
    public void Validate<T>(Asn1Type<T> type, T value)
    {
        if (++_depth > MaxDepth)
        {
            Report(null, $"nesting exceeds {MaxDepth}");
            _depth--;
            return;
        }

        try
        {
            if (type is IAsn1ValidationAdapter adapter)
            {
                adapter.ValidateAdapted(value, this);
                return;
            }

            if (value is null && typeof(T).IsClass)
            {
                Report(null, $"value of {type.Name} is null");
                return;
            }

            var check = type.CheckConstraints(value);
            if (check == ConstraintCheck.Violation)
            {
                Report(null, $"value violates {string.Concat(type.Constraints.Select(c => c.ToString()))} of {type.Name}");
            }
            else if (check == ConstraintCheck.OutsideExtensible)
            {
                Report(null, $"value lies outside the root and additions of the extensible constraint {type.Constraints[^1]} of {type.Name}");
            }

            switch (type)
            {
                case CharacterStringType s when CharacterRepertoire.Validate(s.StringKind, (string)(object)value!) is { } error:
                    Report(null, error);
                    break;
                case TimeType t when TimeSyntax.Validate(t.TimeKind, (string)(object)value!) is { } error:
                    Report(null, error);
                    break;
                case SequenceType<T> seq:
                    foreach (var c in seq.Components)
                    {
                        c.Validate(value, this);
                    }

                    break;
                case ChoiceType<T> choice:
                    {
                        var index = choice.GetIndex(value);
                        if (index >= 0 && index < choice.Alternatives.Length)
                        {
                            choice.Alternatives[index].Validate(value, this);
                        }
                        else if (index >= 0 || choice.GetUnknown(value) is null)
                        {
                            Report(null, $"no valid alternative of {choice.Name} is selected");
                        }

                        break;
                    }

                case EnumeratedType<T> e when e.ItemOf(value) is null:
                    Report(null, $"{value} is not an item of {e.Name}");
                    break;
                default:
                    ValidateCollection(type, value);
                    break;
            }
        }
        finally
        {
            _depth--;
        }
    }

    private void ValidateCollection<T>(Asn1Type<T> type, T value)
    {
        if (type is ICollectionValidator cv)
        {
            cv.ValidateElements(value!, this);
        }
    }
}

/// <summary>
/// Implemented by descriptors that adapt another descriptor (for example the boxing adapters of the dynamic value
/// model): validation is delegated to the adapted descriptor, which knows the value's structure.
/// </summary>
public interface IAsn1ValidationAdapter
{
    /// <summary>Validates <paramref name="value"/> with the adapted descriptor.</summary>
    void ValidateAdapted(object? value, Asn1Validator validator);
}

/// <summary>Implemented by collection descriptors to validate their elements without reflection.</summary>
internal interface ICollectionValidator
{
    void ValidateElements(object list, Asn1Validator validator);
}
