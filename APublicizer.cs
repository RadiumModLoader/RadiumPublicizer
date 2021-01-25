using Mono.Cecil;
using Mono.Collections.Generic;
using System;
using System.IO;
using System.Linq;

namespace APublicizer
{
    public static class APublicizer
    {
        public const string SUFFIX = "-Publicized";

        public static void Main(string[] args)
        {
            var assemblyPath = Path.GetFullPath(args.Single());
            using var publicizer = new Publicizer(assemblyPath);
            var result = publicizer.Run();

            assemblyPath = InsertSuffix(assemblyPath, SUFFIX);
            publicizer.Write(assemblyPath);

            PrintResult(assemblyPath, result);
        }

        private static void PrintResult(string path, Publicizer.PublicizeResult result)
        {
            Console.WriteLine($"Publicized - {path}");

            Console.WriteLine("Publicize result - ");
            Console.WriteLine($"\tTypes - {result.Types}");
            Console.WriteLine($"\tEvents - {result.Events}");
            Console.WriteLine($"\tFields - {result.Fields}");
            Console.WriteLine($"\tMethods - {result.Methods}");
            Console.WriteLine("\tProperties -");
            Console.WriteLine($"\t\tSetters - {result.Property_Setters}");
            Console.WriteLine($"\t\tGetters - {result.Property_Getters}");

            Console.WriteLine("Thank me!");
        }

        private static string InsertSuffix(string path, string suffix)
        {
            var cleanPath = Path.GetDirectoryName(path);
            var filename = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            return Path.Combine(cleanPath!, string.Concat(filename, suffix, extension));
        }

        public static ReaderParameters GetReaderParameters() => new(ReadingMode.Immediate)
        {
            InMemory = true
        };
    }

    public sealed class Publicizer : IDisposable
    {
        public sealed class PublicizeResult
        {
            public uint Types;
            public uint Events;
            public uint Fields;
            public uint Methods;

            public uint Property_Setters;
            public uint Property_Getters;

            public void BumpTypes() => ++Types;
            public void BumpEvents() => ++Events;
            public void BumpFields() => ++Fields;
            public void BumpMethods() => ++Methods;
            public void BumpPropertySetters() => ++Property_Setters;
            public void BumpPropertyGetters() => ++Property_Getters;
        }

        private readonly AssemblyDefinition _assembly;
        private Collection<TypeDefinition>? _types;

        public Publicizer(string path)
        {
            var rParams = APublicizer.GetReaderParameters();
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(path));
            rParams.AssemblyResolver = resolver;

            _assembly = AssemblyDefinition.ReadAssembly(path, rParams);
        }

        public PublicizeResult Run()
        {
            var result = new PublicizeResult();
            DoPublicize(result);
            return result;
        }

        public void Write(string path) => _assembly.Write(path);

        private void DoPublicize(PublicizeResult value)
        {
            DoPublicizeTypes(value);
            DoPublicizeFields(value);
            DoPublicizeMethods(value);
            DoPublicizePropertySetters(value);
            DoPublicizePropertyGetters(value);

            DoFixupEvents(value);
        }

        #region Utility

        private Collection<TypeDefinition> GetTypes()
        {
            if (_types is not null)
                return _types;

            var coll = new Collection<TypeDefinition>();
            void AddType(TypeDefinition definition)
            {
                coll.Add(definition);

                var types = definition.NestedTypes;
                for (var z = 0; z < types.Count; z++)
                    AddType(types[z]);
            }

            var types = _assembly.MainModule.Types;
            for (var z = 0; z < types.Count; z++)
                AddType(types[z]);

            return _types = coll;
        }

        private void Processor<T>(Collection<T> values, Predicate<T> filter, Action<T> processor, Action bump)
        {
            for (var z = 0; z < values.Count; z++)
            {
                var d = values[z];
                if (filter(d))
                {
                    processor(d);
                    bump();
                }
            }
        }

        private void ArrayProcessor<T, R>(Collection<T> values, Func<T, Collection<R>> getter, Action<Collection<R>> processor)
        {
            for (var z = 0; z < values.Count; z++)
            {
                var d = values[z];
                var coll = getter(d);
                processor(coll);
            }
        }

        #endregion

        #region Do

        private void DoPublicizeTypes(PublicizeResult value) =>
            Processor(GetTypes(),
                (t) => t.IsNotPublic,
                (t) => t.IsPublic = true,
                value.BumpTypes);

        private void DoPublicizeFields(PublicizeResult value) =>
            ArrayProcessor<TypeDefinition, FieldDefinition>(GetTypes(),
                (t) => t.Fields,
                (fs) => Processor<FieldDefinition>(fs,
                    (f) => !f.IsPublic,
                    (f) => f.IsPublic = true,
                    value.BumpFields));

        private void DoPublicizeMethods(PublicizeResult value) =>
            ArrayProcessor<TypeDefinition, MethodDefinition>(GetTypes(),
                (t) => t.Methods,
                (ms) => Processor<MethodDefinition>(ms,
                    (m) => !m.IsPublic,
                    (m) => m.IsPublic = true,
                    value.BumpMethods));

        private void DoPublicizePropertySetters(PublicizeResult value) =>
            ArrayProcessor<TypeDefinition, PropertyDefinition>(GetTypes(),
                (t) => t.Properties,
                (ps) => Processor<PropertyDefinition>(ps,
                    (p) => !p.SetMethod?.IsPublic ?? false,
                    (p) => p.SetMethod.IsPublic = true,
                    value.BumpPropertySetters));

        private void DoPublicizePropertyGetters(PublicizeResult value) =>
                        ArrayProcessor<TypeDefinition, PropertyDefinition>(GetTypes(),
                (t) => t.Properties,
                (ps) => Processor<PropertyDefinition>(ps,
                    (p) => !p.GetMethod?.IsPublic ?? false,
                    (p) => p.GetMethod.IsPublic = true,
                    value.BumpPropertyGetters));

        private void DoFixupEvents(PublicizeResult value)
        {
            bool FilterEvent(EventDefinition e)
            {
                // It'll cause a compilation error if I don't do
                e.DeclaringType.Fields.Single(f => f.Name == e.Name).IsPublic = false;
                // Don't lie to users
                value.Fields--;

                return e.AddMethod.IsPrivate || e.RemoveMethod.IsPrivate || (e.InvokeMethod?.IsPrivate ?? false);
            }

            void ProcessEvent(EventDefinition e)
            {
                // 1 event is 3 methods
                value.Methods -= 3;

                e.AddMethod.IsPublic = true;
                e.RemoveMethod.IsPublic = true;

                if (e.InvokeMethod != null)
                    e.InvokeMethod.IsPublic = true;
            }

            ArrayProcessor<TypeDefinition, EventDefinition>(GetTypes(),
                (t) => t.Events,
                (es) => Processor<EventDefinition>(es,
                    FilterEvent,
                    ProcessEvent,
                    value.BumpEvents));
        }

        #endregion

        public void Dispose() => _assembly.Dispose();
    }
}