﻿using Mono.Cecil.Cil;
using MonoMod;
using MonoMod.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace Celeste.Mod.Helpers {
    /// <summary>
    /// Used by the relinker to fill missing gaps.
    /// </summary>
    public static class MonoModUpdateShim {

        public static class _ReflectionHelper {
            [ShimFromAttribute("System.Collections.Generic.Dictionary`2<System.String,System.Reflection.Assembly> MonoMod.Utils.ReflectionHelper::AssemblyCache")]
            public static readonly Dictionary<string, Assembly> AssemblyCache = new Dictionary<string, Assembly>();
        }

        public static class _ILCursor {
            public static void Remove(ILCursor c) => c.Remove();
            public static void GotoLabel(ILCursor c, ILLabel label) => c.GotoLabel(label);
            public static void MarkLabel(ILCursor c, ILLabel label) => c.MarkLabel(label);
            public static void MoveAfterLabel(ILCursor c) => c.MoveAfterLabels();
            public static void MoveBeforeLabel(ILCursor c) => c.MoveBeforeLabels();
            public static IEnumerable<ILLabel> GetIncomingLabels(ILCursor c) => c.IncomingLabels;
            public static void GotoNext(ILCursor c, params Func<Instruction, bool>[] predicates) => c.GotoNext(predicates);
            public static bool TryGotoNext(ILCursor c, params Func<Instruction, bool>[] predicates) => c.TryGotoNext(predicates);
            public static void GotoPrev(ILCursor c, params Func<Instruction, bool>[] predicates) => c.GotoPrev(predicates);
            public static bool TryGotoPrev(ILCursor c, params Func<Instruction, bool>[] predicates) => c.TryGotoPrev(predicates);
            public static void FindNext(ILCursor c, out ILCursor[] cursors, params Func<Instruction, bool>[] predicates) => c.FindNext(out cursors, predicates);
            public static bool TryFindNext(ILCursor c, out ILCursor[] cursors, params Func<Instruction, bool>[] predicates) => c.TryFindNext(out cursors, predicates);
            public static void FindPrev(ILCursor c, out ILCursor[] cursors, params Func<Instruction, bool>[] predicates) => c.FindPrev(out cursors, predicates);
            public static bool TryFindPrev(ILCursor c, out ILCursor[] cursors, params Func<Instruction, bool>[] predicates) => c.TryFindPrev(out cursors, predicates);
            [ShimFromAttribute("System.Int32 MonoMod.Cil.ILCursor::EmitDelegate(System.Action)")]
            public static int EmitDelegate(ILCursor c, Action cb) => c.EmitDelegate(cb);
        }

    }
    internal class ShimFromAttribute : Attribute {
        public string FindableID;
        public ShimFromAttribute(string id) {
            FindableID = id;
        }
    }
}