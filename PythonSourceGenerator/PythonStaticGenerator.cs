﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PythonSourceGenerator.Parser;
using PythonSourceGenerator.Reflection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PythonSourceGenerator;

[Generator(LanguageNames.CSharp)]
public class PythonStaticGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // System.Diagnostics.Debugger.Launch();
        var pythonFilesPipeline = context.AdditionalTextsProvider
            .Where(static text => Path.GetExtension(text.Path) == ".py")
            .Collect();

        context.RegisterSourceOutput(pythonFilesPipeline, static (sourceContext, inputFiles) =>
        {
            foreach (var file in inputFiles)
            {
                // Add environment path
                var @namespace = "Python.Generated"; // TODO : Infer from project

                var fileName = Path.GetFileNameWithoutExtension(file.Path);

                // Convert snakecase to pascal case
                var pascalFileName = string.Join("", fileName.Split('_').Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1)));

                IEnumerable<MethodDefinition> methods;
                // Read the file
                var code = File.ReadAllText(file.Path);

                // Parse the Python file
                var result = PythonSignatureParser.TryParseFunctionDefinitions(code, out var functions, out var errors);

                foreach (var error in errors)
                {
                    // TODO: Match source/target
                    sourceContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PSG004", "PythonStaticGenerator", $"{file.Path} : {error}", "PythonStaticGenerator", DiagnosticSeverity.Error, true), Location.None));
                }

                if (result) { 
                    methods = ModuleReflection.MethodsFromFunctionDefinitions(functions, fileName);
                    string source = FormatClassFromMethods(@namespace, pascalFileName, methods);
                    sourceContext.AddSource($"{pascalFileName}.py.cs", source);
                    sourceContext.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PSG002", "PythonStaticGenerator", $"Generated {pascalFileName}.py.cs", "PythonStaticGenerator", DiagnosticSeverity.Warning, true), Location.None));
                }
            }
        });
    }

    public static string FormatClassFromMethods(string @namespace, string pascalFileName, IEnumerable<MethodDefinition> methods)
    {
        var paramGenericArgs = methods
            .Select(m => m.ParameterGenericArgs)
            .Where(l => l is not null && l.Any());

        return $$"""
            // <auto-generated/>
            using Python.Runtime;
            using PythonEnvironments;
            using PythonEnvironments.CustomConverters;

            using System;
            using System.Collections.Generic;

            namespace {{@namespace}}
            {
                public static class {{pascalFileName}}Extensions
                {
                    private static readonly I{{pascalFileName}} instance = new {{pascalFileName}}Internal();

                    public static I{{pascalFileName}} {{pascalFileName}}(this IPythonEnvironment env)
                    {
                        return instance;
                    }

                    private class {{pascalFileName}}Internal : I{{pascalFileName}}
                    {
                        {{methods.Select(m => m.Syntax).Compile()}}

                        internal {{pascalFileName}}Internal()
                        {
                            {{InjectConverters(paramGenericArgs)}}
                        }
                    }
                }
                public interface I{{pascalFileName}}
                {
                    {{string.Join(Environment.NewLine, methods.Select(m => m.Syntax).Select(m => $"{m.ReturnType.NormalizeWhitespace()} {m.Identifier.Text}{m.ParameterList.NormalizeWhitespace()};"))}}
                }
            }
            """;
    }

    private static string InjectConverters(IEnumerable<IEnumerable<GenericNameSyntax>> paramGenericArgs)
    {
        List<string> encoders = [];
        List<string> decoders = [];

        foreach (var param in paramGenericArgs)
        {
            Process(encoders, decoders, param);
        }

        return string.Join(Environment.NewLine, encoders.Concat(decoders));

        static void Process(List<string> encoders, List<string> decoders, IEnumerable<GenericNameSyntax> param)
        {
            foreach (var genericArg in param)
            {
                var identifier = genericArg.Identifier.Text;
                var converterType = identifier switch
                {
                    "IEnumerable" => $"ListConverter{genericArg.TypeArgumentList}",
                    "IReadOnlyDictionary" => $"DictionaryConverter{genericArg.TypeArgumentList}",
                    "Tuple" => "TupleConverter",
                    _ => throw new NotImplementedException($"No converter for {identifier}")
                };

                var encoder = $"PyObjectConversions.RegisterEncoder(new {converterType}());";
                var decoder = $"PyObjectConversions.RegisterDecoder(new {converterType}());";

                if (!encoders.Contains(encoder))
                {
                    encoders.Add(encoder);
                }

                if (!decoders.Contains(decoder))
                {
                    decoders.Add(decoder);
                }

                // Internally, the DictionaryConverter converts items to a Tuple, so we need the
                // TupleConverter to be registered as well.
                if (identifier == "IReadOnlyDictionary")
                {
                    encoder = $"PyObjectConversions.RegisterEncoder(new TupleConverter());";
                    decoder = $"PyObjectConversions.RegisterDecoder(new TupleConverter());";

                    if (!encoders.Contains(encoder))
                    {
                        encoders.Add(encoder);
                    }

                    if (!decoders.Contains(decoder))
                    {
                        decoders.Add(decoder);
                    }
                }

                var nestedGenerics = genericArg.TypeArgumentList.Arguments.Where(genericArg => genericArg is GenericNameSyntax).Cast<GenericNameSyntax>();
                if (nestedGenerics.Any())
                {
                    Process(encoders, decoders, nestedGenerics);
                }
            }
        }
    }
}