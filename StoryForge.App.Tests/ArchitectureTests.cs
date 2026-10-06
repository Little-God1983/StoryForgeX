using System.Reflection;
using StoryForge.App.ViewModels;
using StoryForge.Engine;

namespace StoryForge.App.Tests;

/// <summary>
/// The UI knows the engine only through IStoryForgeClient, so the engine can later move behind
/// HTTP without touching a view model. Only the composition root (App) may name engine types.
/// </summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly EngineAssembly = typeof(StoryForgeEngineOptions).Assembly;

    [Fact]
    public void No_app_type_outside_the_composition_root_names_an_engine_type()
    {
        var offenders = typeof(MainViewModel).Assembly.GetTypes()
            .Where(type => !IsCompositionRoot(type))
            .SelectMany(type => SignatureTypes(type).Select(used => (type, used)))
            .Where(pair => pair.used.Assembly == EngineAssembly)
            .Select(pair => $"{pair.type.FullName} uses {pair.used.FullName}")
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
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

    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }
        foreach (var field in type.GetFields(All))
        {
            yield return field.FieldType;
        }
        foreach (var property in type.GetProperties(All))
        {
            yield return property.PropertyType;
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
