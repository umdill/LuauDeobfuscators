using MoonSharp.Interpreter;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WRDDeobfuscator;

internal sealed record VmTraceResult(
    string Source,
    string TraceText,
    int EventCount,
    int PayloadOperations,
    string AttemptUsed,
    int RecoveredFunctions);

internal static class VmTracer
{
    private sealed class FunctionInvocation
    {
        public readonly List<string> Arguments = [];
        public readonly List<string> Body = [];
    }

    private sealed class RecoveredFunction
    {
        public int Id;
        public long? EntryState;
        public int ParameterCount;
        public readonly List<FunctionInvocation> Invocations = [];
    }

    private sealed class ActiveInvocation
    {
        public required RecoveredFunction Function;
        public required FunctionInvocation Invocation;
    }

    private sealed class TraceState
    {
        public readonly List<string> Lines = [];
        public readonly List<string> Lua = [];
        public readonly Dictionary<string, string> Services = new(StringComparer.Ordinal);
        public readonly List<long> StatePath = [];
        public readonly HashSet<long> TamperStates = [];
        public readonly Dictionary<int, RecoveredFunction> Functions = [];
        public readonly Stack<ActiveInvocation> InvocationStack = new();
        public const int MaxStructuralDepth = 96;
        public bool StructuralDepthExceeded;
        public long? LastState;
        public int Events;
        public int PayloadOperations;
        public int TamperSuppressions;
        public bool TimedOut;
        public bool Finished;

        public void Event(string line)
        {
            Lines.Add(line);
            Events++;
        }

        public void EmitPayload(string line)
        {
            if (InvocationStack.Count > 0)
                InvocationStack.Peek().Invocation.Body.Add(line);
            else
                Lua.Add(line);

            PayloadOperations++;
        }

        public void EmitSupport(string line)
        {
            if (InvocationStack.Count > 0)
            {
                List<string> body = InvocationStack.Peek().Invocation.Body;
                if (!body.Contains(line, StringComparer.Ordinal))
                    body.Add(line);
            }
            else if (!Lua.Contains(line, StringComparer.Ordinal))
            {
                Lua.Add(line);
            }
        }

        public RecoveredFunction RegisterFunction(int id, long? entryState, int parameterCount)
        {
            if (!Functions.TryGetValue(id, out RecoveredFunction? fn))
            {
                fn = new RecoveredFunction
                {
                    Id = id,
                    EntryState = entryState,
                    ParameterCount = Math.Max(0, parameterCount)
                };
                Functions[id] = fn;
                Event($"FUNCTION_CREATE\t{id}\tentry={(entryState?.ToString(CultureInfo.InvariantCulture) ?? "?")}\tparams={parameterCount}");
            }
            return fn;
        }

        public bool BeginInvocation(RecoveredFunction fn, IReadOnlyList<string> args)
        {
            if (InvocationStack.Count >= MaxStructuralDepth)
            {
                StructuralDepthExceeded = true;
                Event($"FUNCTION_DEPTH_LIMIT\t{fn.Id}\tdepth={InvocationStack.Count}");
                return false;
            }

            var invocation = new FunctionInvocation();
            invocation.Arguments.AddRange(args);
            InvocationStack.Push(new ActiveInvocation { Function = fn, Invocation = invocation });
            Event($"FUNCTION_ENTER\t{fn.Id}\t{string.Join("\t", args.Select(Escape))}");
            return true;
        }

        public void EndInvocation()
        {
            if (InvocationStack.Count == 0)
                return;

            ActiveInvocation active = InvocationStack.Pop();
            FunctionInvocation invocation = active.Invocation;

            if (invocation.Body.Count == 0)
            {
                Event($"FUNCTION_EXIT_EMPTY\t{active.Function.Id}");
                return;
            }

            active.Function.Invocations.Add(invocation);
            string call = $"func_{active.Function.Id}({string.Join(", ", invocation.Arguments)})";

            if (InvocationStack.Count > 0)
                InvocationStack.Peek().Invocation.Body.Add(call);
            else
                Lua.Add(call);

            Event($"FUNCTION_EXIT\t{active.Function.Id}\tbody={invocation.Body.Count}");
        }
    }

    private sealed record AttemptResult(TraceState State, string Name, string Source, Dictionary<long, long> Remap);
    private sealed record InstrumentedSource(string Source, string? StateVariable);

    public static VmTraceResult TryRun(string originalSource, string peeledSource, Action<string>? status = null)
    {
        var attempts = new List<(string Name, string Source)>
        {
            ("original", originalSource),
            ("original-normalized", NormalizeForMoonSharp(originalSource)),
        };

        if (!string.Equals(originalSource, peeledSource, StringComparison.Ordinal))
        {
            attempts.Add(("static-peeled", peeledSource));
            attempts.Add(("peeled-normalized", NormalizeForMoonSharp(peeledSource)));
        }

        AttemptResult? best = null;
        var combinedTrace = new StringBuilder();

        foreach ((string name, string source) in attempts)
        {
            status?.Invoke($"      trying {name}...");
            TraceState state = ExecuteOne(source, name, null, TimeSpan.FromSeconds(2.0), 12_000_000, instrumentClosures: false);
            ReportAttempt(status, name, state);
            AppendTrace(combinedTrace, name, state, null);

            var candidate = new AttemptResult(state, name, source, new Dictionary<long, long>());
            if (best is null || Score(state) > Score(best.State))
                best = candidate;

            if (IsGoodPayload(state))
            {
                AttemptResult enriched = TryStructuralEnrichment(candidate, combinedTrace, status);
                return Finish(enriched, combinedTrace);
            }
        }

        status?.Invoke("      normal VM execution did not reach payload");

        if (best is not null)
        {
            string searchSource = best.Source;
            InstrumentedSource instrumented = InstrumentDispatcher(searchSource);

            if (instrumented.StateVariable is null)
            {
                searchSource = NormalizeForMoonSharp(originalSource);
                instrumented = InstrumentDispatcher(searchSource);
            }

            if (instrumented.StateVariable is not null)
            {
                TraceState baseline = best.State;

                List<long> allStates = ExtractStateCandidates(searchSource, instrumented.StateVariable);
                List<long> blockers = ChooseBlockers(baseline);
                List<long> candidates = PrioritizeCandidates(searchSource, allStates, baseline.StatePath);

                foreach (long observed in baseline.StatePath.Distinct())
                    if (!candidates.Contains(observed))
                        candidates.Add(observed);

                if (blockers.Count == 0 && baseline.LastState is long last)
                    blockers.Add(last);

                status?.Invoke(
                    $"      dispatcher: {instrumented.StateVariable}; observed {baseline.StatePath.Distinct().Count()} unique state(s) " +
                    $"({baseline.StatePath.Count} transitions), blocker(s): " +
                    $"{(blockers.Count == 0 ? "none" : string.Join(",", blockers))}, {candidates.Count} candidate state(s)");

                const int maxSearchRuns = 420;
                int runs = 0;
                var queue = new Queue<Dictionary<long, long>>();
                var seenMaps = new HashSet<string>(StringComparer.Ordinal);

                foreach (long blocker in blockers)
                {
                    foreach (long target in candidates.Take(180))
                    {
                        if (target == blocker)
                            continue;

                        var map = new Dictionary<long, long> { [blocker] = target };
                        string key = MapKey(map);
                        if (seenMaps.Add(key))
                            queue.Enqueue(map);
                    }
                }

                var promising = new List<AttemptResult>();

                while (queue.Count > 0 && runs < maxSearchRuns)
                {
                    Dictionary<long, long> map = queue.Dequeue();
                    runs++;

                    if (runs == 1 || runs % 25 == 0)
                        status?.Invoke($"      state search {runs}/{maxSearchRuns}...");

                    string name = "state-search-" + runs;
                    TraceState state = ExecuteOne(
                        instrumented.Source,
                        name,
                        map,
                        TimeSpan.FromMilliseconds(260),
                        2_400_000,
                        alreadyInstrumented: true,
                        instrumentClosures: false);

                    if (state.PayloadOperations > 0 ||
                        state.TamperStates.Count > 0 ||
                        state.StatePath.Distinct().Count() > baseline.StatePath.Distinct().Count())
                    {
                        AppendTrace(combinedTrace, name, state, map);
                    }

                    var result = new AttemptResult(state, name, searchSource, map);
                    if (Score(state) > Score(best.State))
                        best = result;

                    if (IsGoodPayload(state))
                    {
                        status?.Invoke($"      payload recovered by state search after {runs} probe(s)");
                        AttemptResult enriched = TryStructuralEnrichment(result, combinedTrace, status);
                        return Finish(enriched, combinedTrace);
                    }

                    if (map.Count == 1 && IsPromising(state, baseline))
                        promising.Add(result);
                }

                foreach (AttemptResult first in promising
                             .OrderByDescending(x => Score(x.State))
                             .Take(14))
                {
                    if (runs >= maxSearchRuns)
                        break;

                    foreach (long blocker in ChooseBlockers(first.State))
                    {
                        if (first.Remap.ContainsKey(blocker))
                            continue;

                        foreach (long target in candidates.Take(72))
                        {
                            if (runs >= maxSearchRuns || target == blocker)
                                break;

                            var map = new Dictionary<long, long>(first.Remap)
                            {
                                [blocker] = target
                            };

                            string key = MapKey(map);
                            if (!seenMaps.Add(key))
                                continue;

                            runs++;
                            string name = "state-search-depth2-" + runs;
                            TraceState state = ExecuteOne(
                                instrumented.Source,
                                name,
                                map,
                                TimeSpan.FromMilliseconds(320),
                                3_000_000,
                                alreadyInstrumented: true,
                                instrumentClosures: false);

                            var result = new AttemptResult(state, name, searchSource, map);
                            if (Score(state) > Score(best.State))
                                best = result;

                            if (state.PayloadOperations > 0 ||
                                state.TamperStates.Count > 0 ||
                                state.StatePath.Distinct().Count() > first.State.StatePath.Distinct().Count())
                            {
                                AppendTrace(combinedTrace, name, state, map);
                            }

                            if (IsGoodPayload(state))
                            {
                                status?.Invoke($"      payload recovered by depth-2 state search after {runs} probe(s)");
                                AttemptResult enriched = TryStructuralEnrichment(result, combinedTrace, status);
                                return Finish(enriched, combinedTrace);
                            }
                        }
                    }
                }

                status?.Invoke($"      state search exhausted {runs} probes without a safe payload");
            }
            else
            {
                status?.Invoke("      dispatcher state variable couldnt be found");
            }
        }

        if (best is null)
            return new VmTraceResult("", combinedTrace.ToString(), 0, 0, "none", 0);

        return Finish(best, combinedTrace);
    }

    private static void ReportAttempt(Action<string>? status, string name, TraceState state)
    {
        string extra = state.TamperStates.Count > 0 ? $", tamper state(s): {string.Join(",", state.TamperStates)}" : "";
        status?.Invoke($"      {name}: {state.PayloadOperations} payload op(s), {state.StatePath.Count} VM state(s){extra}");
    }

    private static bool IsGoodPayload(TraceState state)
        => state.PayloadOperations > 0 &&
           !state.Lines.Any(x => x.StartsWith("TRACE_ERROR\t", StringComparison.Ordinal) &&
                                 state.PayloadOperations == 0);

    private static bool IsPromising(TraceState state, TraceState baseline)
        => state.PayloadOperations > 0 ||
           state.StatePath.Distinct().Count() > baseline.StatePath.Distinct().Count() ||
           state.Finished && !baseline.Finished ||
           (state.TamperStates.Count > 0 && !state.TamperStates.SetEquals(baseline.TamperStates));

    private static AttemptResult TryStructuralEnrichment(
        AttemptResult proven,
        StringBuilder combinedTrace,
        Action<string>? status)
    {
        try
        {
            status?.Invoke("      recovering function structure...");

            InstrumentedSource instrumented = InstrumentDispatcher(proven.Source);
            string source = instrumented.StateVariable is null ? proven.Source : instrumented.Source;

            TraceState structural = ExecuteOne(
                source,
                proven.Name + "-structural",
                proven.Remap,
                TimeSpan.FromSeconds(1.5),
                8_000_000,
                alreadyInstrumented: instrumented.StateVariable is not null,
                instrumentClosures: true);

            AppendTrace(combinedTrace, proven.Name + "-structural", structural, proven.Remap);

            int funcs = structural.Functions.Values.Count(f => f.Invocations.Count > 0);
            if (structural.PayloadOperations > 0 && funcs > 0)
            {
                status?.Invoke($"      recovered {funcs} function(s)");
                return new AttemptResult(structural, proven.Name + "-structural", proven.Source, proven.Remap);
            }

            status?.Invoke("      function structure was not recoverable on this path");
        }
        catch (Exception ex)
        {
            status?.Invoke($"      structural pass skipped safely: {ex.GetType().Name}: {ex.Message}");
        }

        return proven;
    }

    private static VmTraceResult Finish(AttemptResult result, StringBuilder combinedTrace)
    {
        TraceState winner = result.State;
        int meaningfulFunctions = winner.Functions.Values.Count(f => f.Invocations.Count > 0);

        string reconstructed = "";
        if (winner.PayloadOperations > 0)
        {
            if (meaningfulFunctions > 0)
            {
                reconstructed =
                    RenderStructuralSource(winner) + "\n";
            }
            else
            {
                StructuralLiftResult lifted = TraceStructuralizer.Lift(winner.Lua);
                if (lifted.AddedStructure)
                {
                    meaningfulFunctions = lifted.Functions;
                    reconstructed = lifted.Source + "\n";
                }
                else
                {
                    reconstructed = CleanOutput(winner.Lua) + "\n";
                }
            }
        }

        return new VmTraceResult(
            reconstructed,
            combinedTrace.ToString(),
            winner.Events,
            winner.PayloadOperations,
            result.Name,
            meaningfulFunctions);
    }

    private static string RenderStructuralSource(TraceState state)
    {
        List<RecoveredFunction> functions = state.Functions.Values
            .Where(f => f.Invocations.Count > 0)
            .OrderBy(f => f.Id)
            .ToList();

        if (functions.Count == 0)
            return CleanOutput(state.Lua);

        var sb = new StringBuilder();

        sb.Append("local ");
        sb.AppendLine(string.Join(", ", functions.Select(f => $"func_{f.Id}")));
        sb.AppendLine();

        foreach (RecoveredFunction fn in functions)
        {
            int argc = Math.Max(
                fn.ParameterCount,
                fn.Invocations.Count == 0 ? 0 : fn.Invocations.Max(i => i.Arguments.Count));

            string parameters = string.Join(", ", Enumerable.Range(1, argc).Select(i => $"arg{i}"));
            sb.Append($"func_{fn.Id} = function({parameters})");
            if (fn.EntryState is long entry)
                sb.Append($" -- recovered VM entry state {entry}");
            sb.AppendLine();

            foreach (string line in GeneralizeFunctionBody(fn))
                sb.Append("    ").AppendLine(line);

            sb.AppendLine("end");
            sb.AppendLine();
        }

        foreach (string line in CleanOutput(state.Lua).Split('\n'))
            if (!string.IsNullOrWhiteSpace(line))
                sb.AppendLine(line);

        return sb.ToString().TrimEnd();
    }

    private static List<string> GeneralizeFunctionBody(RecoveredFunction fn)
    {
        if (fn.Invocations.Count == 0)
            return ["-- body not observed"];

        FunctionInvocation first = fn.Invocations[0];
        int lineCount = first.Body.Count;

        if (fn.Invocations.Any(i => i.Body.Count != lineCount))
            return first.Body.ToList();

        var result = new List<string>();
        for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            string[] lines = fn.Invocations.Select(i => i.Body[lineIndex]).ToArray();

            if (lines.All(x => x == lines[0]))
            {
                result.Add(lines[0]);
                continue;
            }

            string? generalized = TryGeneralizeLine(fn.Invocations, lineIndex);
            result.Add(generalized ?? lines[0] + " -- observed first invocation");
        }

        return result;
    }

    private static string? TryGeneralizeLine(
        IReadOnlyList<FunctionInvocation> invocations,
        int lineIndex)
    {
        if (invocations.Count < 2)
            return null;

        string[] lines = invocations.Select(i => i.Body[lineIndex]).ToArray();
        int maxArgs = invocations.Min(i => i.Arguments.Count);

        for (int argIndex = 0; argIndex < maxArgs; argIndex++)
        {
            string? template = null;
            bool ok = true;

            for (int i = 0; i < invocations.Count; i++)
            {
                string literal = invocations[i].Arguments[argIndex];
                if (string.IsNullOrEmpty(literal) || !lines[i].Contains(literal, StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }

                string candidate = lines[i].Replace(literal, $"arg{argIndex + 1}", StringComparison.Ordinal);
                template ??= candidate;
                if (!string.Equals(template, candidate, StringComparison.Ordinal))
                {
                    ok = false;
                    break;
                }
            }

            if (ok && template is not null)
                return template;
        }

        Match[] prints = lines.Select(x => Regex.Match(
            x,
            @"^(?<fn>print|warn)\((?<quote>[""'])(?<text>.*)\k<quote>\)$",
            RegexOptions.Singleline)).ToArray();

        if (prints.All(m => m.Success))
        {
            for (int argIndex = 0; argIndex < maxArgs; argIndex++)
            {
                string? prefix = null;
                string? suffix = null;
                bool ok = true;

                for (int i = 0; i < invocations.Count; i++)
                {
                    string argLua = invocations[i].Arguments[argIndex];
                    string argText = LuaLiteralText(argLua);
                    string text = LuaText.Unescape(prints[i].Groups["text"].Value);
                    int pos = text.IndexOf(argText, StringComparison.Ordinal);

                    if (pos < 0)
                    {
                        ok = false;
                        break;
                    }

                    string p = text[..pos];
                    string q = text[(pos + argText.Length)..];
                    prefix ??= p;
                    suffix ??= q;

                    if (prefix != p || suffix != q)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok && prefix is not null && suffix is not null)
                {
                    string call = prints[0].Groups["fn"].Value;
                    bool allStrings = invocations.All(i =>
                        i.Arguments.Count > argIndex &&
                        i.Arguments[argIndex].Length >= 2 &&
                        i.Arguments[argIndex][0] == '"' &&
                        i.Arguments[argIndex][^1] == '"');

                    string middle = allStrings ? $"arg{argIndex + 1}" : $"tostring(arg{argIndex + 1})";
                    var parts = new List<string>();
                    if (prefix.Length > 0) parts.Add(LuaText.Quote(prefix));
                    parts.Add(middle);
                    if (suffix.Length > 0) parts.Add(LuaText.Quote(suffix));

                    return $"{call}({string.Join(" .. ", parts)})";
                }
            }
        }

        return null;
    }

    private static string LuaLiteralText(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return LuaText.Unescape(value[1..^1]);
        return value;
    }

    private static int Score(TraceState s)
    {
        int errors = s.Lines.Count(x => x.StartsWith("TRACE_ERROR\t", StringComparison.Ordinal));
        int uniqueStates = s.StatePath.Distinct().Count();
        return s.PayloadOperations * 1_000_000 + uniqueStates * 100 + s.Events - errors * 10_000;
    }

    private static TraceState ExecuteOne(
        string source,
        string attemptName,
        IReadOnlyDictionary<long, long>? remap,
        TimeSpan wallBudget,
        int instructionBudget,
        bool alreadyInstrumented = false,
        bool instrumentClosures = false)
    {
        var state = new TraceState();

        try
        {
            Script script = new(CoreModules.Preset_Complete);
            ConfigureSandbox(script, state);

            DynValue tableValue = script.Globals.Get("table");
            if (tableValue.Type == DataType.Table)
            {
                DynValue unpack = tableValue.Table.Get("unpack");
                if (!unpack.IsNil()) script.Globals["unpack"] = unpack;
            }

            script.Globals["getfenv"] = DynValue.NewCallback((ctx, args) => DynValue.NewTable(script.Globals));
            script.Globals["setfenv"] = DynValue.NewCallback((ctx, args) => args.Count > 0 ? args[0] : DynValue.Nil);
            script.Globals["newproxy"] = DynValue.NewCallback((ctx, args) => NewProxy(script, args));

            script.Globals["__wrd_state"] = DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count == 0 || args[0].Type != DataType.Number)
                    return args.Count > 0 ? args[0] : DynValue.Nil;

                double raw = args[0].Number;
                long key = checked((long)Math.Round(raw));
                state.LastState = key;
                if (state.StatePath.Count == 0 || state.StatePath[^1] != key)
                    state.StatePath.Add(key);

                if (remap is not null && remap.TryGetValue(key, out long target))
                {
                    state.Event($"STATE_REMAP\t{key}\t{target}");
                    state.LastState = target;
                    if (state.StatePath.Count == 0 || state.StatePath[^1] != target)
                        state.StatePath.Add(target);
                    return DynValue.NewNumber(target);
                }

                return args[0];
            });

            script.Globals["__wrd_enter"] = DynValue.NewCallback((ctx, args) =>
            {
                if (args.Count < 3)
                    return DynValue.Nil;

                int id = args[0].Type == DataType.Number
                    ? (int)Math.Round(args[0].Number)
                    : state.Functions.Count + 1;
                long? entry = args[1].Type == DataType.Number
                    ? checked((long)Math.Round(args[1].Number))
                    : null;
                int parameterCount = args[2].Type == DataType.Number
                    ? Math.Max(0, (int)Math.Round(args[2].Number))
                    : 0;

                RecoveredFunction fn = state.RegisterFunction(id, entry, parameterCount);
                string[] luaArgs = args.GetArray().Skip(3).Select(ValueLua).ToArray();
                bool entered = state.BeginInvocation(fn, luaArgs);
                return DynValue.NewBoolean(entered);
            });

            script.Globals["__wrd_exit"] = DynValue.NewCallback((ctx, args) =>
            {
                state.EndInvocation();
                return DynValue.Nil;
            });

            state.Event("ATTEMPT\t" + attemptName);

            string executable = source;
            if (!alreadyInstrumented)
                executable = InstrumentDispatcher(source).Source;

            if (instrumentClosures)
            {
                executable = InstrumentClosureFactories(executable);

                script.DoString(@"
function __wrd_wrap(fn, entry, argc, id)
    return function(...)
        local entered = __wrd_enter(id, entry, argc, ...)
        local r = { fn(...) }
        if entered then __wrd_exit(id) end
        return unpack(r)
    end
end
");
            }

            DynValue chunk = script.LoadString(executable, codeFriendlyName: "input.lua");
            DynValue coroutine = script.CreateCoroutine(chunk);

            const int autoYield = 5_000;
            coroutine.Coroutine.AutoYieldCounter = autoYield;
            int maxYields = Math.Max(1, instructionBudget / autoYield);
            int yields = 0;
            var stopwatch = Stopwatch.StartNew();

            DynValue result = coroutine.Coroutine.Resume();
            while (result.Type == DataType.YieldRequest)
            {
                yields++;
                if (yields >= maxYields || stopwatch.Elapsed >= wallBudget)
                {
                    state.TimedOut = true;
                    state.Event($"TRACE_TIMEOUT\t{attemptName}\tyields={yields}\tms={stopwatch.ElapsedMilliseconds}");
                    break;
                }
                result = coroutine.Coroutine.Resume();
            }

            if (!state.TimedOut && result.Type != DataType.YieldRequest)
            {
                state.Finished = true;
                state.Event("FINISHED\t" + attemptName);
            }
        }
        catch (Exception ex)
        {
            state.Event("TRACE_ERROR\t" + Escape(ex.GetType().Name + ": " + ex.Message));
        }

        return state;
    }

    private sealed record FunctionFactory(
        int ReplaceStart,
        int ReplaceEnd,
        string ClosureName,
        string EntryExpression,
        int ParameterCount);

    private static string InstrumentClosureFactories(string source)
    {
        List<FunctionFactory> factories = FindFunctionFactories(source);
        if (factories.Count == 0)
            return source;

        var sb = new StringBuilder(source);
        for (int i = factories.Count - 1; i >= 0; i--)
        {
            FunctionFactory f = factories[i];
            string replacement = $"__wrd_wrap({f.ClosureName},{f.EntryExpression},{f.ParameterCount},{i + 1})";
            sb.Remove(f.ReplaceStart, f.ReplaceEnd - f.ReplaceStart);
            sb.Insert(f.ReplaceStart, replacement);
        }

        return sb.ToString();
    }

    private static List<FunctionFactory> FindFunctionFactories(string source)
    {
        var result = new List<FunctionFactory>();
        var rx = new Regex(@"local\s+(?<name>[A-Za-z_]\w*)\s*=\s*function\s*\(", RegexOptions.CultureInvariant);

        foreach (Match m in rx.Matches(source))
        {
            string name = m.Groups["name"].Value;
            int functionPos = source.IndexOf("function", m.Index, StringComparison.Ordinal);
            if (functionPos < 0)
                continue;

            int open = source.IndexOf('(', functionPos);
            int close = FindMatchingParen(source, open);
            if (open < 0 || close < 0)
                continue;

            int functionEnd = FindLuaBlockEnd(source, functionPos);
            if (functionEnd < 0)
                continue;

            string parameters = source[(open + 1)..close].Trim();
            int parameterCount = CountParameters(parameters);
            string body = source[(close + 1)..functionEnd];

            Match dispatch = Regex.Match(
                body,
                @"return\s+[A-Za-z_]\w*\s*\(\s*(?<entry>[A-Za-z_]\w*|-?\d+(?:\s*[+\-*/%^]\s*-?\d+)*)\s*,\s*\{",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!dispatch.Success)
                continue;

            int afterEnd = functionEnd + 3;
            int cursor = SkipTrivia(source, afterEnd);
            if (!StartsWord(source, cursor, "return"))
                continue;

            cursor = SkipTrivia(source, cursor + 6);
            if (!StartsWord(source, cursor, name))
                continue;

            int returnNameEnd = cursor + name.Length;
            result.Add(new FunctionFactory(
                cursor,
                returnNameEnd,
                name,
                dispatch.Groups["entry"].Value.Trim(),
                parameterCount));
        }

        return result;
    }

    private static int CountParameters(string parameters)
    {
        if (parameters.Length == 0 || parameters == "...")
            return 0;

        return parameters.Split(',')
            .Select(x => x.Trim())
            .Count(x => x.Length > 0 && x != "...");
    }

    private static int FindMatchingParen(string source, int open)
    {
        if (open < 0 || open >= source.Length || source[open] != '(')
            return -1;

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (SkipLuaTriviaOrString(source, ref i))
                continue;

            if (source[i] == '(')
                depth++;
            else if (source[i] == ')' && --depth == 0)
                return i;
        }

        return -1;
    }

    private static int FindLuaBlockEnd(string source, int functionPos)
    {
        int depth = 0;
        for (int i = functionPos; i < source.Length; i++)
        {
            if (SkipLuaTriviaOrString(source, ref i))
                continue;

            if (!IsIdentifierStart(source[i]))
                continue;

            int tokenStart = i;
            i++;
            while (i < source.Length && IsIdentifierPart(source[i]))
                i++;
            string token = source[tokenStart..i];
            i--;

            if (token is "function" or "if" or "do" or "repeat")
            {
                depth++;
                continue;
            }

            if (token is "end" or "until")
            {
                depth--;
                if (depth == 0)
                    return tokenStart;
            }
        }

        return -1;
    }

    private static bool SkipLuaTriviaOrString(string source, ref int i)
    {
        char c = source[i];

        if (c is '\'' or '"')
        {
            char quote = c;
            for (i++; i < source.Length; i++)
            {
                if (source[i] == '\\')
                {
                    i++;
                    continue;
                }
                if (source[i] == quote)
                    return true;
            }
            return true;
        }

        if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
        {
            int longOpen = i + 2;
            if (TryLongBracket(source, longOpen, out int eq, out int contentStart))
            {
                string close = "]" + new string('=', eq) + "]";
                int end = source.IndexOf(close, contentStart, StringComparison.Ordinal);
                i = end < 0 ? source.Length - 1 : end + close.Length - 1;
                return true;
            }

            int nl = source.IndexOf('\n', i + 2);
            i = nl < 0 ? source.Length - 1 : nl;
            return true;
        }

        if (c == '[' && TryLongBracket(source, i, out int equals, out int start))
        {
            string close = "]" + new string('=', equals) + "]";
            int end = source.IndexOf(close, start, StringComparison.Ordinal);
            i = end < 0 ? source.Length - 1 : end + close.Length - 1;
            return true;
        }

        return false;
    }

    private static bool TryLongBracket(string source, int index, out int equals, out int contentStart)
    {
        equals = 0;
        contentStart = -1;
        if (index < 0 || index >= source.Length || source[index] != '[')
            return false;

        int i = index + 1;
        while (i < source.Length && source[i] == '=')
        {
            equals++;
            i++;
        }

        if (i >= source.Length || source[i] != '[')
            return false;

        contentStart = i + 1;
        return true;
    }

    private static int SkipTrivia(string source, int index)
    {
        int i = Math.Clamp(index, 0, source.Length);
        while (i < source.Length)
        {
            if (char.IsWhiteSpace(source[i]) || source[i] == ';')
            {
                i++;
                continue;
            }

            if (source[i] == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                int nl = source.IndexOf('\n', i + 2);
                i = nl < 0 ? source.Length : nl + 1;
                continue;
            }
            break;
        }
        return i;
    }

    private static bool StartsWord(string source, int index, string word)
    {
        if (index < 0 || index + word.Length > source.Length)
            return false;
        if (!source.AsSpan(index, word.Length).SequenceEqual(word.AsSpan()))
            return false;

        bool left = index == 0 || !IsIdentifierPart(source[index - 1]);
        int end = index + word.Length;
        bool right = end >= source.Length || !IsIdentifierPart(source[end]);
        return left && right;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static InstrumentedSource InstrumentDispatcher(string source)
    {
        var rx = new Regex(@"while\s+(?<state>[A-Za-z_]\w*)\s+do\s+if\s+\k<state>\s*[<>]",
            RegexOptions.Singleline);
        Match m = rx.Match(source);
        if (!m.Success)
            return new InstrumentedSource(source, null);

        string state = m.Groups["state"].Value;
        int whileStart = m.Index;
        int doPos = source.IndexOf("do", whileStart, StringComparison.Ordinal);
        if (doPos < 0)
            return new InstrumentedSource(source, null);

        int insert = doPos + 2;
        string injected = source.Insert(insert, $" {state}=__wrd_state({state}) ");
        return new InstrumentedSource(injected, state);
    }

    private static List<long> ExtractStateCandidates(string source, string stateVariable)
    {
        var values = new HashSet<long>();
        string v = Regex.Escape(stateVariable);

        foreach (Match m in Regex.Matches(source,
                     $@"\b{v}\s*=\s*(?<expr>[+\-0-9()*/%^\s]+)", RegexOptions.Singleline))
        {
            AddArithmeticPrefix(values, m.Groups["expr"].Value);
        }

        foreach (Match m in Regex.Matches(source,
                     $@"\b{v}\s*=\s*[A-Za-z_]\w*\s+and\s+(?<a>[+\-0-9()*/%^\s]+?)\s*or\s+(?<b>[+\-0-9()*/%^\s]+)",
                     RegexOptions.Singleline))
        {
            AddArithmeticPrefix(values, m.Groups["a"].Value);
            AddArithmeticPrefix(values, m.Groups["b"].Value);
        }

        foreach (Match m in Regex.Matches(source,
                     @"\b(?:and|or)\s+(?<expr>[+\-0-9()*/%^\s]+)", RegexOptions.Singleline))
        {
            AddArithmeticPrefix(values, m.Groups["expr"].Value);
        }

        return values
            .Where(x => x > 0 && x < 100_000_000)
            .Distinct()
            .ToList();
    }

    private static void AddArithmeticPrefix(HashSet<long> values, string text)
    {
        string s = text.Trim();
        if (s.Length == 0) return;

        if (IntegerExpression.TryEvaluate(s, out long direct))
        {
            values.Add(direct);
            return;
        }

        for (int i = s.Length - 1; i > 0; i--)
        {
            string part = s[..i].TrimEnd();
            if (part.Length == 0) continue;
            if (IntegerExpression.TryEvaluate(part, out long value))
            {
                values.Add(value);
                return;
            }
        }
    }

    private static List<long> ChooseBlockers(TraceState state)
    {
        if (state.TamperStates.Count > 0)
            return state.TamperStates.ToList();

        return state.StatePath
            .AsEnumerable()
            .Reverse()
            .Distinct()
            .Take(3)
            .ToList();
    }

    private static List<long> PrioritizeCandidates(string source, List<long> all, List<long> path)
    {
        var ordered = new List<long>();
        var seen = new HashSet<long>();

        int tamper = source.IndexOf("Tamper Detected!", StringComparison.OrdinalIgnoreCase);
        if (tamper >= 0)
        {
            int start = Math.Max(0, tamper - 5000);
            int len = Math.Min(source.Length - start, 10000);
            string window = source.Substring(start, len);
            foreach (long value in all)
            {
                if (window.Contains(value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) && seen.Add(value))
                    ordered.Add(value);
            }
        }

        var visited = path.ToHashSet();
        foreach (long value in all.Where(x => !visited.Contains(x)))
            if (seen.Add(value)) ordered.Add(value);
        foreach (long value in all.Where(visited.Contains))
            if (seen.Add(value)) ordered.Add(value);

        return ordered;
    }

    private static string MapKey(IReadOnlyDictionary<long, long> map)
        => string.Join(";", map.OrderBy(x => x.Key).Select(x => x.Key + ">" + x.Value));

    private static void AppendTrace(StringBuilder sb, string name, TraceState state, IReadOnlyDictionary<long, long>? remap)
    {
        sb.AppendLine($"===== ATTEMPT: {name} =====");
        if (remap is not null && remap.Count > 0)
            sb.AppendLine("REMAP=" + MapKey(remap));
        if (state.StatePath.Count > 0)
            sb.AppendLine("STATE_PATH=" + string.Join(",", state.StatePath));
        foreach (string line in state.Lines)
            sb.AppendLine(line);
        sb.AppendLine();
    }

    private static void ConfigureSandbox(Script script, TraceState state) // Tamper Guard suppression :3
    {
        script.Globals["io"] = DynValue.Nil;
        script.Globals["os"] = DynValue.Nil;
        script.Globals["require"] = DynValue.Nil;
        script.Globals["dofile"] = DynValue.Nil;
        script.Globals["loadfile"] = DynValue.Nil;

        script.Globals["error"] = DynValue.NewCallback((ctx, args) =>
        {
            string message = args.Count > 0 ? ValueText(args[0]) : "error";
            if (message.Contains("Tamper Detected!", StringComparison.OrdinalIgnoreCase))
            {
                state.TamperSuppressions++;
                if (state.LastState is long tamperState)
                    state.TamperStates.Add(tamperState);
                state.Event("TAMPER_GUARD_SUPPRESSED\t" + Escape(message) +
                    (state.LastState is long s ? "\tstate=" + s.ToString(CultureInfo.InvariantCulture) : ""));
                return DynValue.Nil;
            }

            throw new ScriptRuntimeException(message);
        });

        script.Globals["assert"] = DynValue.NewCallback((ctx, args) =>
        {
            bool ok = args.Count > 0 && args[0].CastToBool();
            if (ok) return args.Count > 0 ? args[0] : DynValue.True;
            string message = args.Count > 1 ? ValueText(args[1]) : "assertion failed!";
            throw new ScriptRuntimeException(message);
        });

        script.Globals["warn"] = DynValue.NewCallback((ctx, args) =>
        {
            DynValue[] vals = args.GetArray();
            state.Event("WARN\t" + string.Join("\t", vals.Select(v => Escape(ValueText(v)))));
            state.EmitPayload($"warn({string.Join(", ", vals.Select(ValueLua))})");
            return DynValue.Nil;
        });

        script.Globals["print"] = DynValue.NewCallback((ctx, args) =>
        {
            DynValue[] vals = args.GetArray();
            state.Event("PRINT\t" + string.Join("\t", vals.Select(v => Escape(ValueText(v)))));
            state.EmitPayload($"print({string.Join(", ", vals.Select(ValueLua))})");
            return DynValue.Nil;
        });

        Table debug = new(script);
        debug["traceback"] = DynValue.NewCallback((ctx, args) => DynValue.NewString("input.lua:1"));
        debug["getinfo"] = DynValue.NewCallback((ctx, args) =>
        {
            Table info = new(script);
            info["source"] = "@input.lua";
            info["short_src"] = "input.lua";
            info["what"] = "Lua";
            info["currentline"] = 1;
            info["linedefined"] = 1;
            info["lastlinedefined"] = 1;
            return DynValue.NewTable(info);
        });
        script.Globals["debug"] = DynValue.NewTable(debug);

        foreach (string name in new[]
        {
            "game", "workspace", "script", "Enum", "Instance", "Vector2", "Vector3",
            "CFrame", "Color3", "UDim", "UDim2", "BrickColor", "TweenInfo", "Drawing",
            "NumberRange", "NumberSequence", "ColorSequence", "Rect", "Ray"
        })
            script.Globals[name] = MakeProxy(script, state, name);

        script.Globals["task"] = MakeTask(script);
        script.Globals["wait"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));
        script.Globals["tick"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));
        script.Globals["time"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));

        foreach (string name in new[]
        {
            "request", "http_request", "writefile", "readfile", "appendfile", "delfile",
            "makefolder", "listfiles", "setclipboard", "getgc", "getconnections",
            "hookfunction", "hookmetamethod", "getrawmetatable", "setreadonly"
        })
            script.Globals[name] = MakeProxy(script, state, name);

        script.Globals["getgenv"] = DynValue.NewCallback((ctx, args) => DynValue.NewTable(script.Globals));
        script.Globals["getrenv"] = DynValue.NewCallback((ctx, args) => DynValue.NewTable(script.Globals));
        script.Globals["checkcaller"] = DynValue.NewCallback((ctx, args) => DynValue.False);

        script.Globals["loadstring"] = DynValue.NewCallback((ctx, args) =>
        {
            string code = args.Count > 0 ? args[0].CastToString() ?? "" : "";
            state.Event("LOADSTRING\t" + Escape(code));
            if (!string.IsNullOrWhiteSpace(code))
            {
                state.EmitPayload("-- recovered loadstring payload");
                foreach (string line in code.Replace("\r", "").Split('\n'))
                    state.EmitPayload(line);
            }
            return DynValue.NewCallback((c, a) => DynValue.Nil);
        });
    }

    private static DynValue NewProxy(Script script, CallbackArguments args)
    {
        Table t = new(script);
        Table mt = new(script);
        t.MetaTable = mt;
        return DynValue.NewTable(t);
    }

    private static DynValue MakeTask(Script script)
    {
        Table t = new(script);
        t["wait"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));
        foreach (string n in new[] { "spawn", "defer", "delay" })
        {
            t[n] = DynValue.NewCallback((ctx, args) =>
            {
                DynValue[] vals = args.GetArray();
                DynValue? fn = vals.FirstOrDefault(v => v.Type is DataType.Function or DataType.ClrFunction);
                if (fn is not null)
                {
                    try
                    {
                        DynValue[] rest = vals.Where(v => !ReferenceEquals(v, fn)).ToArray();
                        script.Call(fn, rest);
                    }
                    catch { }
                }
                return DynValue.Nil;
            });
        }
        return DynValue.NewTable(t);
    }

    private static DynValue MakeProxy(Script script, TraceState state, string path)
    {
        Table t = new(script);
        Table mt = new(script);

        mt["__index"] = DynValue.NewCallback((ctx, args) =>
        {
            string key = args[1].CastToString() ?? args[1].ToString();
            string child = path + "." + key;
            state.Event("GET\t" + Escape(child));

            if (key == "GetService")
            {
                return DynValue.NewCallback((c, a) =>
                {
                    DynValue[] vals = a.GetArray();
                    string service = vals.LastOrDefault(v => v.Type == DataType.String)?.String ?? "Service";
                    state.Event("SERVICE\t" + Escape(service));
                    if (!state.Services.TryGetValue(service, out string? alias))
                    {
                        alias = SafeIdentifier(service);
                        state.Services[service] = alias;
                        state.EmitSupport($"local {alias} = game:GetService({LuaText.Quote(service)})");
                    }
                    return MakeProxy(script, state, alias);
                });
            }

            if (key == "Connect")
            {
                return DynValue.NewCallback((c, a) =>
                {
                    DynValue? fn = a.GetArray().FirstOrDefault(x => x.Type is DataType.Function or DataType.ClrFunction);
                    state.Event("CONNECT\t" + Escape(path));

                    int before = state.Lua.Count;
                    state.EmitPayload($"{path}:Connect(function(...)");
                    if (fn is not null)
                    {
                        try
                        {
                            script.Call(fn,
                                MakeProxy(script, state, "arg1"),
                                MakeProxy(script, state, "arg2"),
                                MakeProxy(script, state, "arg3"));
                        }
                        catch (Exception ex)
                        {
                            state.Event("CALLBACK_ERROR\t" + Escape(ex.Message));
                        }
                    }
                    state.EmitPayload("end)");
                    return MakeProxy(script, state, path + ".Connection");
                });
            }

            return MakeProxy(script, state, child);
        });

        mt["__newindex"] = DynValue.NewCallback((ctx, args) =>
        {
            string key = args[1].CastToString() ?? args[1].ToString();
            string lhs = path + "." + key;
            state.Event("SET\t" + Escape(lhs) + "\t" + Escape(ValueText(args[2])));
            state.EmitPayload($"{lhs} = {ValueLua(args[2])}");
            return DynValue.Nil;
        });

        mt["__call"] = DynValue.NewCallback((ctx, args) =>
        {
            DynValue[] vals = args.GetArray();
            state.Event("CALL\t" + Escape(path));
            state.EmitPayload($"{path}({string.Join(", ", vals.Skip(1).Select(ValueLua))})");
            return MakeProxy(script, state, path + "()");
        });

        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString(path));
        mt["__len"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));
        mt["__eq"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
        mt["__lt"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
        mt["__le"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(false));
        foreach (string op in new[] { "__add", "__sub", "__mul", "__div", "__mod", "__pow", "__unm" })
            mt[op] = DynValue.NewCallback((c, a) => DynValue.NewNumber(0));

        t.MetaTable = mt;
        return DynValue.NewTable(t);
    }

    private static string NormalizeForMoonSharp(string source)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(
            source,
            @"(?<=\d)--(?=\d)",
            " - -");
        return s;
    }

    private static string CleanOutput(List<string> lines)
    {
        var output = new List<string>();
        string? previous = null;
        foreach (string line in lines)
        {
            if (line.StartsWith("local ", StringComparison.Ordinal) && line == previous)
                continue;
            output.Add(line);
            previous = line;
        }
        return string.Join("\n", output);
    }

    private static string SafeIdentifier(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        string v = sb.Length == 0 ? "Service" : sb.ToString();
        if (char.IsDigit(v[0])) v = "S" + v;
        return v;
    }

    private static string ValueText(DynValue v) => v.Type switch
    {
        DataType.String => v.String,
        DataType.Number => v.Number.ToString(CultureInfo.InvariantCulture),
        DataType.Boolean => v.Boolean ? "true" : "false",
        DataType.Nil or DataType.Void => "nil",
        _ => v.ToString()
    };

    private static string ValueLua(DynValue v) => v.Type switch
    {
        DataType.String => LuaText.Quote(v.String),
        DataType.Number => v.Number.ToString(CultureInfo.InvariantCulture),
        DataType.Boolean => v.Boolean ? "true" : "false",
        DataType.Nil or DataType.Void => "nil",
        DataType.Table => LuaText.Quote(v.ToString()),
        _ => LuaText.Quote(v.ToString())
    };

    private static string Escape(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
}
