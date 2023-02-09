using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.InlineRT;
using MonoMod.Utils;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ICustomAttributeProvider = Mono.Cecil.ICustomAttributeProvider;
using MethodAttributes = Mono.Cecil.MethodAttributes;

namespace MonoMod {
#region Helper Patch Attributes
    /// <summary>
    /// Make the marked method the new entry point.
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.MakeEntryPoint))]
    class MakeEntryPointAttribute : Attribute { }

    /// <summary>
    /// Helper for patching methods force-implemented by an interface
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchInterface))]
    class PatchInterfaceAttribute : Attribute { }

    /// <summary>
    /// Forcibly changes a given member's name.
    /// </summary>
    [MonoModCustomAttribute(nameof(MonoModRules.ForceName))]
    class ForceNameAttribute : Attribute {
        public ForceNameAttribute(string name) {}
    }

    /// <summary>
    /// Patches the attributed method to replace _initblk calls with the initblk opcode.
    /// </summary>
    [MonoModCustomMethodAttribute(nameof(MonoModRules.PatchInitblk))]
    class PatchInitblkAttribute : Attribute { }
#endregion

    static partial class MonoModRules {

        public static readonly bool IsPatchingGame;
        public static readonly ModuleDefinition RulesModule;

        static MonoModRules() {
            // Note: It may actually be too late to set this to false.
            MonoModRule.Modder.MissingDependencyThrow = false;

            // Always write portable PDBs
            if (MonoModRule.Modder.WriterParameters.WriteSymbols)
                MonoModRule.Modder.WriterParameters.SymbolWriterProvider = new PortablePdbWriterProvider();

            // Get our own ModuleDefinition (this is the best way to do this afaik)
            string execModName = Assembly.GetExecutingAssembly().GetName().Name;
            RulesModule = MonoModRule.Modder.DependencyMap.Keys.First(mod =>
                execModName == $"{mod.Name.Substring(0, mod.Name.Length - 4)}.MonoModRules [MMILRT, ID:{MonoModRulesManager.GetId(MonoModRule.Modder)}]"
            );

            // Determine if we're patching the game itself or a mod
            IsPatchingGame = MonoModRule.Modder.FindType("Celeste.Celeste")?.SafeResolve()?.Scope == MonoModRule.Modder.Module;

            // Initialize the appropriate rules
            InitCommonRules(MonoModRule.Modder);
            if (IsPatchingGame)
                InitGameRules(MonoModRule.Modder);
            else
                InitModRules(MonoModRule.Modder);
        }

        private static void InitCommonRules(MonoModder modder) {
            // Check the modder version
            string monoModderAsmName = typeof(MonoModder).Assembly.GetName().Name;
            AssemblyNameReference monoModderAsmRef = RulesModule.AssemblyReferences.First(a => a.Name == monoModderAsmName);
            if (MonoModder.Version < monoModderAsmRef.Version)
                throw new Exception($"Unexpected version of MonoMod patcher: {MonoModder.Version} (expected {monoModderAsmRef.Version}+)");

            // Replace XNA assembly references with FNA ones
            ReplaceAssemblyRefs(MonoModRule.Modder, static asm => asm.Name.StartsWith("Microsoft.Xna.Framework"), GetRulesAssemblyRef("FNA"));

            // Ensure that FNA.dll can be loaded
            if (MonoModRule.Modder.FindType("Microsoft.Xna.Framework.Game")?.SafeResolve() == null)
                throw new Exception("Failed to resolve Microsoft.Xna.Framework.Game");

            // Add common post processor
            modder.PostProcessors += CommonPostProcessor;
        }

        public static void CommonPostProcessor(MonoModder modder) {
            // Patch debuggable attribute
            // We can't get the attribute from our own assembly (because it's a temporary MonoMod one), so get it from the entry assembly (which is MiniInstaller)
            DebuggableAttribute everestAttr = Assembly.GetEntryAssembly().GetCustomAttribute<DebuggableAttribute>();
            CustomAttribute celesteAttr = modder.Module.Assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == typeof(DebuggableAttribute).FullName);
            if (celesteAttr != null && everestAttr != null) {
                celesteAttr.ConstructorArguments[0] = new CustomAttributeArgument(modder.Module.ImportReference(typeof(DebuggableAttribute.DebuggingModes)), everestAttr.DebuggingFlags);
            }

            // Replace assembly name versions (fixes stubbed steam DLLs under Linux)
            foreach (AssemblyNameReference asmRef in modder.Module.AssemblyReferences) {
                ModuleDefinition dep = modder.DependencyMap[modder.Module].FirstOrDefault(mod => mod.Assembly.Name.Name == asmRef.Name);
                if (dep != null)
                    asmRef.Version = dep.Assembly.Name.Version;
            }

            // Post process types
            foreach (TypeDefinition type in modder.Module.Types)
                PostProcessType(modder, type);
        }

        private static void PostProcessType(MonoModder modder, TypeDefinition type) {
            // Fix enumerator decompilation
            if (type.IsCompilerGeneratedEnumerator())
                FixEnumeratorDecompile(type);

            // Fix short-long opcodes
            foreach (MethodDefinition method in type.Methods)
                method.FixShortLongOps();

            // Post-process nested types
            foreach (TypeDefinition nested in type.NestedTypes)
                PostProcessType(modder, nested);
        }


        public static AssemblyName GetRulesAssemblyRef(string name) => Assembly.GetExecutingAssembly().GetReferencedAssemblies().First(asm => asm.Name.Equals(name));

        public static bool ReplaceAssemblyRefs(MonoModder modder, Func<AssemblyNameReference, bool> filter, AssemblyName newRef) {
            bool hasNewRef = false;
            for (int i = 0; i < modder.Module.AssemblyReferences.Count; i++) {
                AssemblyNameReference asmRef = modder.Module.AssemblyReferences[i];
                if (asmRef.Name.Equals(newRef.Name))
                    hasNewRef = true;
                else if(filter(asmRef)) {
                    // Remove dependency
                    modder.Module.AssemblyReferences.RemoveAt(i--);
                    modder.DependencyMap[modder.Module].RemoveAll(dep => dep.Assembly.FullName == asmRef.FullName);
                }
            }

            if (!hasNewRef) {
                // Add new dependency and map it
                AssemblyNameReference asmNameRef = new AssemblyNameReference(newRef.Name, newRef.Version);
                modder.Module.AssemblyReferences.Add(asmNameRef);
                modder.MapDependency(modder.Module, asmNameRef);
            }

            return !hasNewRef;
        }

#region Helper Patches
        public static void MakeEntryPoint(MethodDefinition method, CustomAttribute attrib) {
            MonoModRule.Modder.Module.EntryPoint = method;
        }

        public static void PatchInterface(MethodDefinition method, CustomAttribute attrib) {
            MethodAttributes flags = MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
            method.Attributes |= flags;
        }

        public static void ForceName(ICustomAttributeProvider cap, CustomAttribute attrib) {
            if (cap is IMemberDefinition member)
                member.Name = (string) attrib.ConstructorArguments[0].Value;
        }

        public static void PatchInitblk(ILContext il, CustomAttribute attrib) {
            ILCursor c = new ILCursor(il);
            while (c.TryGotoNext(i => i.MatchCall(out MethodReference mref) && mref.Name == "_initblk")) {
                c.Next.OpCode = OpCodes.Initblk;
                c.Next.Operand = null;
            }
        }

        /// <summary>
        /// Fix ILSpy unable to decompile enumerator after MonoMod patching<br />
        /// (<code>yield-return decompiler failed: Unexpected instruction in Iterator.Dispose()</code>)
        /// </summary>
        public static void FixEnumeratorDecompile(TypeDefinition type) {
            foreach (MethodDefinition method in type.Methods) {
                new ILContext(method).Invoke(il => {
                    ILCursor cursor = new ILCursor(il);
                    while (cursor.TryGotoNext(instr => instr.MatchCallvirt(out MethodReference m) &&
                        (m.Name is "System.Collections.IEnumerable.GetEnumerator" or "System.IDisposable.Dispose" ||
                            m.Name.StartsWith("<>m__Finally")))
                    ) {
                        cursor.Next.OpCode = OpCodes.Call;
                    }
                });
            }
        }
#endregion

    }
}
