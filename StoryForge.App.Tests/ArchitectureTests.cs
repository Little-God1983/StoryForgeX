using System.Reflection;
using Microsoft.Extensions.Options;
using StoryForge.App.ViewModels;
using StoryForge.Engine;

namespace StoryForge.App.Tests;

/// <summary>
/// The UI knows the engine only through IStoryForgeClient, so the engine can later move behind
/// HTTP without touching a view model. Only the composition root (App) may name engine types.
/// The check reads type signatures; a method body that news up an engine type is not seen.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly EngineAssembly = typeof(StoryForgeEngineOptions).Assembly;

    [Fact]
    public void No_app_type_outside_the_composition_root_names_an_engine_type()
    {
        var offenders = typeof(MainViewModel).Assembly.GetTypes()
            .Where(type => !IsCompositionRoot(type))
            .SelectMany(type => EngineTypesNamedBy(type).Select(used => $"{type.FullName} uses {used.FullName}"))
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(typeof(SampleWithGenericField))]
    [InlineData(typeof(SampleWithArrayProperty))]
    [InlineData(typeof(SampleWithGenericMethodParameter))]
    [InlineData(typeof(SampleImplementingGenericInterface))]
    public void The_check_finds_engine_types_wrapped_in_other_types(Type sample)
    {
        Assert.Contains(typeof(StoryForgeEngineOptions), EngineTypesNamedBy(sample));
    }

    private sealed class SampleWithGenericField
    {
        public IOptions<StoryForgeEngineOptions>? Options = null;
    }

    private sealed class SampleWithArrayProperty
    {
        public StoryForgeEngineOptions[] All { get; } = [];
    }

    private sealed class SampleWithGenericMethodParameter
    {
        public static void Use(Lazy<StoryForgeEngineOptions> _) { }
    }

    private sealed class SampleImplementingGenericInterface : IComparer<StoryForgeEngineOptions>
    {
        public int Compare(StoryForgeEngineOptions? x, StoryForgeEngineOptions? y) => 0;
    }

    private static bool IsCompositionRoot(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current == typeof(App))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<Type> EngineTypesNamedBy(Type type) =>
        SignatureTypes(type).SelectMany(Unwrap).Where(used => used.Assembly == EngineAssembly).Distinct();

    /// <summary>The type itself plus everything it is built from: generic arguments and element types.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }
        }
        if (type.IsGenericType)
        {
            foreach (var inner in type.GetGenericArguments().SelectMany(Unwrap))
            {
                yield return inner;
            }
        }
    }

    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }
        foreach (var implemented in type.GetInterfaces())
        {
            yield return implemented;
        }
        foreach (var field in type.GetFields(All))
        {
            yield return field.FieldType;
        }
        foreach (var property in type.GetProperties(All))
        {
            yield return property.PropertyType;
        }
        foreach (var handler in type.GetEvents(All))
        {
            yield return handler.EventHandlerType!;
        }
        foreach (var method in type.GetMethods(All))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
        foreach (var constructor in type.GetConstructors(All))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }
}
