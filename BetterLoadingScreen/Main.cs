using BepInEx;
using HarmonyLib;
using HG;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using RoR2;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;

namespace BetterLoadingScreen
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Main : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "Gorakh";
        public const string PluginName = "BetterLoadingScreen";
        public const string PluginVersion = "1.0.0";

        static Type RoR2Application_InitializeGameRoutine_IteratorType;

        readonly record struct SystemInitializerCall(MethodInfo InitMethod, object Instance, object[] Arguments)
        {
            public readonly void Invoke()
            {
                InitMethod.Invoke(Instance, Arguments);
            }
        }
        static readonly Queue<SystemInitializerCall> _initializerCallQueue = new Queue<SystemInitializerCall>();

        const float SYSTEM_INITIALIZER_START_PERCENT = 0.7f;

        void Awake()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            Log.Init(Logger);

            Type loadGameContentIterator = findIteratorType(typeof(RoR2Application), nameof(RoR2Application.LoadGameContent));
            if (loadGameContentIterator is not null)
            {
                MethodInfo moveNext = findIteratorMoveNextMethod(loadGameContentIterator);
                if (moveNext is not null)
                {
                    new ILHook(moveNext, il =>
                    {
                        ILCursor c = new ILCursor(il);

                        while (c.TryGotoNext(MoveType.After,
                                             x => x.MatchCallOrCallvirt(AccessTools.DeclaredPropertyGetter(typeof(ReadableProgress<float>), nameof(ReadableProgress<float>.value)))))
                        {
                            c.EmitDelegate((float value) =>
                            {
                                return Util.Remap(value, 0f, 1f, 0f, SYSTEM_INITIALIZER_START_PERCENT);
                            });
                        }
                    });
                }
            }

            RoR2Application_InitializeGameRoutine_IteratorType = findIteratorType(typeof(RoR2Application), nameof(RoR2Application.InitializeGameRoutine));

            if (RoR2Application_InitializeGameRoutine_IteratorType is not null)
            {
                MethodInfo moveNextMethod = findIteratorMoveNextMethod(RoR2Application_InitializeGameRoutine_IteratorType);
                if (moveNextMethod is not null)
                {
                    new ILHook(moveNextMethod, RoR2Application_InitializeGameRoutine_MoveNext);

                    IL.RoR2.SystemInitializerAttribute.Execute += SystemInitializerAttribute_Execute;
                }
            }

            stopwatch.Stop();
            Log.Info_NoCallerPrefix($"Initialized in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        }

        static void SystemInitializerAttribute_Execute(ILContext il)
        {
            ILCursor c = new ILCursor(il);

            if (c.TryGotoNext(MoveType.Before,
                              x => x.MatchCallOrCallvirt(SymbolExtensions.GetMethodInfo<MethodBase>(_ => _.Invoke(default, default)))))
            {
                c.Remove();
                c.EmitDelegate((MethodInfo initMethod, object instance, object[] args) =>
                {
                    _initializerCallQueue.Enqueue(new SystemInitializerCall(initMethod, instance, args));
                    return (object)null;
                });
            }

            // Don't set hasExecuted to true, since none of the methods have actually been run yet
            if (c.TryGotoNext(MoveType.Before, x => x.MatchStsfld<SystemInitializerAttribute>(nameof(SystemInitializerAttribute.hasExecuted))))
            {
                c.Emit(OpCodes.Pop);
                c.Emit(OpCodes.Ldc_I4_0);
            }
        }

        static void RoR2Application_InitializeGameRoutine_MoveNext(ILContext il)
        {
            FieldInfo iteratorState_FI = RoR2Application_InitializeGameRoutine_IteratorType.GetField("<>1__state", BindingFlags.NonPublic | BindingFlags.Instance);
            if (iteratorState_FI is null)
            {
                Log.Error("Unable to find iterator state field");
                return;
            }

            FieldInfo iteratorCurrent_FI = RoR2Application_InitializeGameRoutine_IteratorType.GetField("<>2__current", BindingFlags.NonPublic | BindingFlags.Instance);
            if (iteratorCurrent_FI is null)
            {
                Log.Error("Unable to find iterator current field");
                return;
            }

            ILCursor c = new ILCursor(il);

            ILLabel[] stateSwitchLabels = Array.Empty<ILLabel>();
            if (!c.TryGotoNext(MoveType.Before, x => x.MatchSwitch(out stateSwitchLabels)))
            {
                Log.Error("Unable to find iterator state switch");
                return;
            }

            int appendState(ILLabel targetLabel)
            {
                int stateIndex = stateSwitchLabels.Length;

                Array.Resize(ref stateSwitchLabels, stateSwitchLabels.Length + 1);
                stateSwitchLabels[stateIndex] = targetLabel;

                return stateIndex;
            }

            Instruction stateSwitchInstruction = c.Next;

            if (!c.TryGotoNext(MoveType.After,
                               x => x.MatchCallOrCallvirt(SymbolExtensions.GetMethodInfo(() => SystemInitializerAttribute.Execute()))))
            {
                Log.Error("Unable to find SystemInitializer Execute call");
                return;
            }

            ILLabel newSwitchStateLabel = c.DefineLabel();
            int newState = appendState(newSwitchStateLabel);

            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<object>>(runSystemInitializersAsync);
            c.Emit(OpCodes.Stfld, iteratorCurrent_FI);

            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldc_I4, newState);
            c.Emit(OpCodes.Stfld, iteratorState_FI);

            c.Emit(OpCodes.Ldc_I4_1);
            c.Emit(OpCodes.Ret);

            c.MarkLabel(newSwitchStateLabel);

            stateSwitchInstruction.Operand = stateSwitchLabels;
        }

        static IEnumerator runSystemInitializersAsync()
        {
#if DEBUG
            Log.Debug("Running injected enumerable state");
#endif

            TMP_Text loadingPercentIndicatorLabel = (from label in FindObjectsOfType<TMP_Text>()
                                                     where label.name.Equals("LoadingPercentIndicator", StringComparison.Ordinal)
                                                     select label).FirstOrDefault();

            Animation loadingPercentAnimation = loadingPercentIndicatorLabel ? loadingPercentIndicatorLabel.GetComponentInChildren<Animation>() : null;

            StringBuilder loadingTextStringBuilder = HG.StringBuilderPool.RentStringBuilder();

            int numCallsCompleted = 0;
            int totalCalls = _initializerCallQueue.Count;
            while (_initializerCallQueue.Count > 0)
            {
                SystemInitializerCall systemInitializerCall = _initializerCallQueue.Dequeue();

                systemInitializerCall.Invoke();
                numCallsCompleted++;

                float progress = Util.Remap(numCallsCompleted / (float)totalCalls, 0f, 1f, SYSTEM_INITIALIZER_START_PERCENT, 1f);
                if (loadingPercentIndicatorLabel)
                {
                    loadingTextStringBuilder.Clear();
                    loadingTextStringBuilder.AppendInt(Mathf.FloorToInt(progress * 100f), 1U, uint.MaxValue);
                    loadingTextStringBuilder.Append("%");

                    loadingPercentIndicatorLabel.SetText(loadingTextStringBuilder);
                }

                if (loadingPercentAnimation)
                {
                    AnimationClip clip = loadingPercentAnimation.clip;
                    clip.SampleAnimation(loadingPercentAnimation.gameObject, progress * 0.99f * clip.length);
                }

                yield return 0;
            }

            HG.StringBuilderPool.ReturnStringBuilder(loadingTextStringBuilder);

            SystemInitializerAttribute.hasExecuted = true;
        }

        static Type findIteratorType(Type declaringType, string iteratorMethodName)
        {
            Type iteratorType = declaringType.GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(t => typeof(IEnumerator<object>).IsAssignableFrom(t) && t.Name.StartsWith($"<{iteratorMethodName}>d__"));
            if (iteratorType is null)
            {
                Log.Error($"Unable to find {iteratorMethodName} iterator class");
                return null;
            }

            Log.Info($"Found {iteratorMethodName} iterator class: {iteratorType.FullName}");
            return iteratorType;
        }

        static MethodInfo findIteratorMoveNextMethod(Type iteratorType)
        {
            MethodInfo moveNextMethod = iteratorType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNextMethod is null)
            {
                Log.Error($"Unable to find {iteratorType.FullName} iterator MoveNext method");
                return null;
            }

            return moveNextMethod;
        }
    }
}
