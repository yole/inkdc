using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Ink.Runtime;

namespace inkdc
{
    public class StoryDecompiler
    {
        public StoryDecompiler(Story story)
        {
            Story = story;
        }

        public Story Story { get; }
        private StringBuilder result = new StringBuilder();
        public int ChoiceNesting { get; set; }

        public string DecompileRoot()
        {
            Container mainContentContainer = Story.mainContentContainer;
            if (mainContentContainer.namedOnlyContent != null)
            {
                Ink.Runtime.Object globalContainer;
                if (mainContentContainer.namedOnlyContent.TryGetValue("global decl", out globalContainer))
                {
                    AnalyzeContainer(globalContainer as Container).Decompile(this);
                }
            }
            AnalyzeContainer(mainContentContainer).Decompile(this);
            if (mainContentContainer.namedOnlyContent != null)
            {
                foreach (string name in mainContentContainer.namedOnlyContent.Keys)
                {
                    if (name == "global decl")
                    {
                        continue;
                    }
                    var namedContent = mainContentContainer.namedOnlyContent[name];
                    if (namedContent is Container namedContainer)
                    {
                        Out("== " + name + " ==\n");
                        DecompileKnot(namedContainer);
                    }
                }
            }
            
            return result.ToString();
        }

        public void Out(string text)
        {
            result.Append(text);
        }

        public void EnsureNewLine()
        {
            if (result.Length > 0 && result[result.Length - 1] != '\n')
            {
                result.Append("\n");
            }
        }

        void DecompileKnot(Container container)
        {
            AnalyzeContainer(container).Decompile(this);

            if (container.namedOnlyContent != null)
            {
                foreach (string name in container.namedOnlyContent.Keys)
                {
                    var namedContent = container.namedOnlyContent[name];
                    if (namedContent is Container namedContainer)
                    {
                        Out("= " + name + "\n");
                        AnalyzeContainer(namedContainer).Decompile(this);
                    }
                }
            }
        }

        public CompiledContainer AnalyzeContainer(Container container, int index = 0, int endIndex = -1)
        {
            List<ICompiledStructure> result = new();
            if (endIndex < 0)
            {
                endIndex = container.content.Count;
            }
            while (index < endIndex)
            {
                var weave = AnalyzeWeave(container, ref index);
                if (weave != null)
                {
                    result.Add(weave);
                    continue;
                }
                var multiConditional = AnalyzeMultilineConditional(container, ref index);
                if (multiConditional != null)
                {
                    result.Add(multiConditional);
                    continue;
                }
                if (container.content[index].IsControlCommand(ControlCommand.CommandType.EvalStart))
                {
                    var switchStatement = AnalyzeSwitch(container, ref index);
                    if (switchStatement != null)
                    {
                        result.Add(switchStatement);
                        continue;
                    }
                    var choice = AnalyzeChoice(container, ref index);
                    if (choice != null)
                    {
                        result.Add(choice);
                        continue;
                    }
                    var sequence = AnalyzeSequence(container, ref index);
                    if (sequence != null)
                    {
                        result.Add(sequence);
                        continue;
                    }
                    var conditional = AnalyzeConditional(container, ref index);
                    if (conditional != null)
                    {
                        result.Add(conditional);
                        continue;
                    }
                    var embedded = AnalyzeEmbeddedExpression(container, ref index);
                    if (embedded != null)
                    {
                        result.AddRange(embedded);
                        continue;
                    }
                }
                if (container.content[index] is Container child)
                {
                    result.Add(AnalyzeContainer(child));
                }
                else
                {
                    result.Add(new CompiledObject(container.content[index]));
                }
                index++;
            }
            return new CompiledContainer(result);
        }

        ChoiceData AnalyzeChoice(Container container, ref int index)
        {
            if (!container.content[index].IsControlCommand(ControlCommand.CommandType.EvalStart))
            {
                return null;
            }
            var content = container.content;
            var choiceStart = index;
            var choicePointIndex = content.FindIndex(choiceStart, (x) => x is ChoicePoint);
            if (choicePointIndex < 0) return null;

            ChoicePoint choicePoint = (ChoicePoint)content[choicePointIndex];
            ChoiceData choiceData = new(choicePoint);
            ContainerCursor cursor = new(container, choiceStart);
            cursor.SkipControlCommand(ControlCommand.CommandType.EvalStart);

            if (choicePoint.hasStartContent)
            {
                Container startContentContainer = (Container)cursor.Container.namedContent["s"];
                // last element is divert to $r, need to skip it from decompilation
                choiceData.StartContent = AnalyzeContainer(startContentContainer, 0, startContentContainer.content.Count - 1);
                if (cursor.SkipToLabel("$r1"))
                {
                    cursor.SkipControlCommand(ControlCommand.CommandType.EndString);
                }
            }
            if (choicePoint.hasChoiceOnlyContent)
            {
                cursor.SkipControlCommand(ControlCommand.CommandType.BeginString);
                int startIndex = cursor.Index;
                cursor.SkipToControlCommand(ControlCommand.CommandType.EndString);
                choiceData.ChoiceOnlyContent = AnalyzeContainer(container, startIndex, cursor.Index - 1);
            }
            if (choicePoint.hasCondition)
            {
                int startIndex = cursor.Index;
                cursor.SkipToControlCommand(ControlCommand.CommandType.EvalEnd);
                choiceData.Condition = AnalyzeExpression(container, startIndex, cursor.Index - 1);
            }

            var target = Story.ContentAtPath(choicePoint.pathOnChoice).container;
            if (target != null)
            {
                ContainerCursor targetCursor = new(target);
                if (choicePoint.hasStartContent)
                {
                    targetCursor.SkipToLabel("$r2");
                }
                choiceData.InnerContent = AnalyzeContainer(target, targetCursor.Index);
            }

            index = choicePointIndex + 1;
            return choiceData;
        }

        WeaveData AnalyzeWeave(Container container, ref int index)
        {
            List<ChoiceData> choices = new();
            int localIndex = index;
            while (true)
            {
                if (localIndex < container.content.Count &&
                    container.content [localIndex] is Container childContainer)
                {
                    var childIndex = 0;
                    ChoiceData choice = AnalyzeChoice(childContainer, ref childIndex);
                    if (choice == null) break;
                    choices.Add(choice);
                    localIndex++;
                }
                else
                {
                    break;
                }
            }
            if (choices.Count == 0) return null;

            Path maybeGatherPath = ExtractGatherPath(choices [0]);
            if (maybeGatherPath != null)
            {
                for (int i = 1; i < choices.Count; i++)
                {
                    Path otherPath = ExtractGatherPath(choices[i]);
                    if (otherPath == null || maybeGatherPath.componentsString != otherPath.componentsString)
                    {
                        maybeGatherPath = null;
                        break;
                    }
                }
            }
            CompiledContainer gatherContent = null;
            Path outerGatherPath = null;
            if (maybeGatherPath != null)
            {
                SearchResult searchResult = Story.ContentAtPath(maybeGatherPath);
                if (searchResult.correctObj != null)
                {
                    foreach (var choice in choices)
                    {
                        var lastObj = choice.InnerContent.Last();
                        if (lastObj is CompiledObject obj && obj.Obj is Divert)
                        {
                            choice.InnerContent.RemoveAt(choice.InnerContent.Count - 1);
                        }
                        else 
                        {
                            var weave = LastWeaveForChoice(choice);
                            if (weave != null)
                            {
                                weave.GatherContent = null;
                            }
                        }
                    }
                    var gatherContainer = searchResult.container != null ? searchResult.container : searchResult.correctObj.parent as Container;
                    var gatherIndex = searchResult.container != null ? 0 : maybeGatherPath.lastComponent.index;
                    gatherContent = AnalyzeContainer(gatherContainer, gatherIndex);
                    outerGatherPath = maybeGatherPath;
                }
            }
            index = localIndex;
            return new WeaveData(choices, gatherContent, outerGatherPath);
        }

        Path ExtractGatherPath(ChoiceData choice)
        {
            if (choice.InnerContent.Count == 0) return null;
            if (choice.InnerContent.Last() is CompiledObject obj && obj.Obj is Divert divert)
            {
                return divert.targetPath;
            }
            WeaveData weaveData = LastWeaveForChoice(choice);
            if (weaveData != null)
            {
                return weaveData.OuterGatherPath;
            }

            return null;
        }

        WeaveData LastWeaveForChoice(ChoiceData choice)
        {
            if (choice.InnerContent.Last() is CompiledContainer container &&
                container.At(container.Count - 1) is WeaveData weaveData)
            {
                while (weaveData.GatherContent.Last() is WeaveData nextWeaveData)
                {
                    weaveData = nextWeaveData;
                }
                return weaveData;
            }
            return null;
        }

        SequenceData AnalyzeSequence(Container container, ref int index)
        {
            var cursor = new ContainerCursor(container, index);
            cursor.SkipControlCommand(ControlCommand.CommandType.EvalStart);
            var cycle = false;
            var shuffle = false;
            if (!cursor.SkipControlCommand(ControlCommand.CommandType.VisitIndex))
            {
                return null;
            }
            if (cursor.Current is IntValue)
            {
                cursor.TakeNext();
            }
            if (cursor.Current is NativeFunctionCall functionCall && functionCall.name == "%")
            {
                cycle = true;
            }
            else if (cursor.Current.IsControlCommand(ControlCommand.CommandType.SequenceShuffleIndex))
            {
                shuffle = true;
            }
            var branches = new List<CompiledContainer>();
            while (!cursor.AtEnd() && !cursor.Current.IsControlCommand(ControlCommand.CommandType.NoOp))
            {
                if (cursor.Current is Divert divert && IsSequenceBranchDivert(divert))
                {
                    var branchContainer = divert.targetPointer.container;
                    if (branchContainer != null)
                    {
                        // first element is a PopGeneratedValue(), last is divert back
                        branches.Add(AnalyzeContainer(branchContainer, 1, branchContainer.content.Count - 1));
                    }
                }
                cursor.TakeNext();
            }
            if (cursor.AtEnd())
            {
                return null;
            }
            cursor.SkipControlCommand(ControlCommand.CommandType.NoOp);
            index = cursor.Index;
            return new SequenceData(branches, cycle, shuffle);
        }

        private static bool IsSequenceBranchDivert(Divert divert)
        {
            Pointer targetPointer = divert.targetPointer;
            if (targetPointer.container == null) return false;
            var name = targetPointer.container.name;
            return name != null && name.StartsWith("s") && targetPointer.index == 0;
        }

        ConditionalData AnalyzeConditional(Container container, ref int index)
        {
            var cursor = new ContainerCursor(container, index);
            cursor.SkipControlCommand(ControlCommand.CommandType.EvalStart);
            int conditionStart = cursor.Index;
            if (!cursor.SkipToControlCommand(ControlCommand.CommandType.EvalEnd))
            {
                return null;
            }
            int conditionEnd = cursor.Index - 1;
            List<CompiledContainer> branches = new();
            while (cursor.Current is Container nestedContainer)
            {
                if (nestedContainer.content[0] is Divert divert)
                {
                    var branchContainer = divert.targetPointer.container;
                    // last element is divert to rejoin target
                    branches.Add(AnalyzeContainer(branchContainer, 0, branchContainer.content.Count - 1));
                }
                else
                {
                    return null;
                }
                cursor.TakeNext();
            }
            if (cursor.Current.IsControlCommand(ControlCommand.CommandType.NoOp))
            {
                cursor.TakeNext();
                index = cursor.Index;
                return new ConditionalData(new() { AnalyzeExpression(container, conditionStart, conditionEnd) },
                    branches);
            }

            return null;
        }

        ConditionalData AnalyzeMultilineConditional(Container container, ref int index, ICompiledStructure discriminator = null)
        {
            var branches = new List<CompiledContainer>();
            var conditions = new List<ICompiledStructure>();
            for (int curIndex = index;
                curIndex < container.content.Count && container.content[curIndex] is Container child;
                curIndex++)
            {
                var childCursor = new ContainerCursor(child);
                if (discriminator != null && !(childCursor.Current is Divert))
                {
                    if (childCursor.Current.IsControlCommand(ControlCommand.CommandType.Duplicate))
                    {
                        childCursor.TakeNext();
                    }
                    else
                    {
                        return null;
                    }
                }
                if (childCursor.Current.IsControlCommand(ControlCommand.CommandType.EvalStart))
                {
                    childCursor.TakeNext();
                    int conditionStart = childCursor.Index;
                    if (!childCursor.SkipToControlCommand(ControlCommand.CommandType.EvalEnd))
                    {
                        return null;
                    }
                    if (childCursor.Current is Divert divert && divert.isConditional)
                    {
                        int conditionEnd = childCursor.Index - 1;
                        if (discriminator != null)
                        {
                            // skip == operation which compares against value pushed by Duplicate
                            conditionEnd--;
                        }
                        conditions.Add(AnalyzeExpression(child, conditionStart, conditionEnd));
                        var branchContainer = divert.targetPointer.container;
                        // last element is divert to rejoin target; first element is 'pop' if discriminator is specified
                        branches.Add(AnalyzeContainer(branchContainer, discriminator != null ? 1 : 0, branchContainer.content.Count - 1));
                        index = curIndex + 1;
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (childCursor.Current is Divert divert && !divert.isConditional && branches.Count > 0)
                {
                    var branchContainer = divert.targetPointer.container;
                    // last element is divert to rejoin target; first element is 'pop' if discriminator is specified
                    branches.Add(AnalyzeContainer(branchContainer, discriminator != null ? 1 : 0, branchContainer.content.Count - 1));
                    index = curIndex + 1;
                    break;
                }
                else
                {
                    return null;
                }
            }

            if (branches.Count > 0)
            {
                if (index < container.content.Count &&
                    container.content[index].IsControlCommand(ControlCommand.CommandType.NoOp))
                {
                    index++;
                }
                return new ConditionalData(conditions, branches, discriminator);
            }
            return null;
        }

        ConditionalData AnalyzeSwitch(Container container, ref int index)
        {
            var endIndex = index;
            ICompiledStructure discriminator = AnalyzeEvalBlock(container, ref endIndex);
            if (discriminator == null) return null;
            var conditional = AnalyzeMultilineConditional(container, ref endIndex, discriminator);
            if (conditional != null)
            {
                index = endIndex;
            }
            return conditional;
        }

        List<ICompiledStructure> AnalyzeEmbeddedExpression(Container container, ref int index)
        {
            var result = new List<ICompiledStructure>();
            var cursor = new ContainerCursor(container, index);
            cursor.SkipControlCommand(ControlCommand.CommandType.EvalStart);
            while (true)
            {
                int initializerStartIndex = cursor.Index;
                if (!cursor.SkipToVarAssign())
                {
                    break;
                }
                var varAssign = container.content[cursor.Index - 1] as VariableAssignment;
                var endIndex = cursor.Index - 1;
                if (container.content[endIndex - 1].IsControlCommand(ControlCommand.CommandType.EvalEnd))
                {
                    // eval end precedes varassign
                    var initializer = AnalyzeExpression(container, initializerStartIndex, cursor.Index - 2);
                    index = cursor.Index;
                    return new() { new VariableAssignmentExpression(varAssign.variableName, initializer,
                        varAssign.isGlobal, varAssign.isNewDeclaration) };
                }
                else
                {
                    var initializer = AnalyzeExpression(container, initializerStartIndex, cursor.Index - 1);
                    result.Add(new VariableAssignmentExpression(varAssign.variableName, initializer,
                        varAssign.isGlobal, varAssign.isNewDeclaration));
                }
            }

            int expressionStart = cursor.Index;
            if (!cursor.SkipToControlCommand(ControlCommand.CommandType.EvalEnd))
            {
                return null;
            }
            if (cursor.Index - expressionStart > 1)
            {
                if (cursor.Container.content[cursor.Index - 2].IsControlCommand(ControlCommand.CommandType.EvalOutput))
                {
                    result.Add(new EmbeddedExpression(AnalyzeExpression(container, expressionStart, cursor.Index - 2)));
                }
                else
                {
                    result.Add(new StatementExpression(AnalyzeExpression(container, expressionStart, cursor.Index - 1)));
                }
            }
            index = cursor.Index;
            return result;
        }

        public ICompiledStructure AnalyzeEvalBlock(Container container, ref int index)
        {
            var cursor = new ContainerCursor(container);
            if (!cursor.Current.IsControlCommand(ControlCommand.CommandType.EvalStart))
            {
                return null;
            }
            cursor.TakeNext();
            int startIndex = cursor.Index;
            if (!cursor.SkipToControlCommand(ControlCommand.CommandType.EvalEnd))
            {
                return null;
            }
            if (cursor.Index == startIndex + 1)
            {
                // EvalStart immediately followed by EvalEnd
                return null;
            }
            ICompiledStructure expression = AnalyzeExpression(container, startIndex, cursor.Index - 1, true);
            if (expression != null)
            {
                index = cursor.Index;
            }
            return expression;
        }

        public ICompiledStructure AnalyzeExpression(Container container, int startIndex, int endIndex, bool ignoreUnknown = false)
        {
            List<Ink.Runtime.Object> expression = container.content.GetRange(startIndex, endIndex - startIndex);
            Stack<ICompiledStructure> stack = new();
            foreach (Ink.Runtime.Object obj in expression)
            {
                if (obj is VariableReference varRef)
                {
                    stack.Push(new CompiledVarRef(varRef));
                }
                else if (obj is NativeFunctionCall call)
                {
                    if (call.name == "!")
                    {
                        var operand = stack.Pop();
                        stack.Push(new UnaryExpression("not", operand));
                    }
                    else if (_binaryOperators.Contains(call.name))
                    {
                        BuildBinaryExpression(stack, call.name);
                    }
                    else if (call.name == "POW")
                    {
                        BuildFunctionCall(stack, "POW", 2);
                    }
                    else if (call.name == "INT" || call.name == "FLOOR" || call.name == "FLOAT")
                    {
                        BuildFunctionCall(stack, call.name, 1);
                    }
                    else
                    {
                        if (ignoreUnknown) return null;
                        throw new NotSupportedException("Don't know how to decompile " + obj);
                    }
                }
                else if (obj is Value)
                {
                    stack.Push(new CompiledValue(obj as Value));
                }
                else if (obj is ControlCommand controlCommand)
                {
                    if (controlCommand.commandType == ControlCommand.CommandType.ChoiceCount)
                    {
                        stack.Push(new FunctionCall("CHOICE_COUNT", new()));
                    }
                    else if (controlCommand.commandType == ControlCommand.CommandType.Turns)
                    {
                        stack.Push(new FunctionCall("TURNS", new()));
                    }
                    else if (controlCommand.commandType == ControlCommand.CommandType.TurnsSince)
                    {
                        BuildFunctionCall(stack, "TURNS_SINCE", 1);
                    }
                    else if (controlCommand.commandType == ControlCommand.CommandType.Random)
                    {
                        BuildFunctionCall(stack, "RANDOM", 2);
                    }
                    else if (controlCommand.commandType == ControlCommand.CommandType.BeginString ||
                        controlCommand.commandType == ControlCommand.CommandType.EndString)
                    {
                        // TODO ignore for now
                    }
                    else
                    {
                        if (ignoreUnknown) return null;
                        throw new NotSupportedException("Don't know how to decompile " + obj);
                    }
                }
                else
                {
                    if (ignoreUnknown) return null;
                    throw new NotSupportedException("Don't know how to decompile " + obj);
                }

            }
            return stack.Pop();
        }

        void BuildBinaryExpression(Stack<ICompiledStructure> stack, string op)
        {
            var operand1 = stack.Pop();
            var operand2 = stack.Pop();
            stack.Push(new BinaryExpression(op, operand2, operand1));
        }

        void BuildFunctionCall(Stack<ICompiledStructure> stack, string functionName, int argCount)
        {
            var args = new List<ICompiledStructure>();
            for (int i = 0; i < argCount; i++)
            {
                args.Insert(0, stack.Pop());
            }
            stack.Push(new FunctionCall(functionName, args));
        }

        static HashSet<string> _binaryOperators = new()
        {
            "+", "-", "/", "*", "%", "==", "<", ">", ">=", "<=", "!=", "&&", "||", "?", "!?"
        };
    }

    public interface ICompiledStructure
    {
        void Decompile(StoryDecompiler dc);
    }

    class CompiledObject : ICompiledStructure
    {
        public Ink.Runtime.Object Obj { get; private set; }

        public CompiledObject(Ink.Runtime.Object obj)
        {
            Obj = obj;
        }

        public void Decompile(StoryDecompiler dc)
        {
            if (Obj is StringValue stringValue)
            {
                dc.Out(stringValue.value);
            }
            else if (Obj is Divert divert)
            {
                if (!IsGeneratedDivert(dc, divert))
                {
                    dc.Out("-> " + divert.targetPathString + "\n");
                }
            }
            else if (Obj is ControlCommand controlCommand)
            {
                if (controlCommand.commandType != ControlCommand.CommandType.Done &&
                    controlCommand.commandType != ControlCommand.CommandType.End)
                {
                    throw new NotSupportedException("Don't know how to decompile " + Obj);
                }
            }
            else
            {
                throw new NotSupportedException("Don't know how to decompile " + Obj);
            }
        }

        private bool IsGeneratedDivert(StoryDecompiler dc, Divert divert)
        {
            SearchResult searchResult = dc.Story.ContentAtPath(divert.targetPath);
            if (searchResult.container != null && searchResult.container.IsDone())
            {
                return true;
            }
            if (divert.parent is Container container && container.content.Count == 1 &&
                divert.targetPath.lastComponent.index == 0)
            {
                // divert to first stitch in a knot
                return true;
            }
            return false;
        }
    }

    public class CompiledContainer : ICompiledStructure, IEnumerable<ICompiledStructure>
    {
        private readonly List<ICompiledStructure> content;

        public CompiledContainer(List<ICompiledStructure> content)
        {
            this.content = content;
        }

        public void Decompile(StoryDecompiler dc)
        {
            foreach (var child in content)
            {
                child.Decompile(dc);
            }
        }

        public int Count => content.Count;
        public ICompiledStructure At(int i) => content[i];
        public ICompiledStructure Last() => content[content.Count - 1];

        public void RemoveAt(int index)
        {
            content.RemoveAt(index);
        }

        public IEnumerator<ICompiledStructure> GetEnumerator()
        {
            return ((IEnumerable<ICompiledStructure>)content).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)content).GetEnumerator();
        }
    }

    class CompiledValue : ICompiledStructure
    {
        private readonly Value value;

        public CompiledValue(Value value)
        {
            this.value = value;
        }

        public void Decompile(StoryDecompiler dc)
        {
            if (value is DivertTargetValue divertTargetValue)
            {
                dc.Out("-> " + divertTargetValue.CompactPathString(divertTargetValue.targetPath));
            }
            else if (value is StringValue stringValue)
            {
                dc.Out("\"" + stringValue + "\"");
            }
            else
            {
                dc.Out(value.ToString());
            }
        }
    }

    class CompiledVarRef : ICompiledStructure
    {
        private readonly VariableReference varRef;

        public CompiledVarRef(VariableReference varRef)
        {
            this.varRef = varRef;
        }

        public void Decompile(StoryDecompiler dc)
        {
            if (varRef.name != null)
            {
                dc.Out(varRef.name);
            }
            else
            {
                dc.Out(varRef.pathStringForCount);
            }
        }
    }

    class UnaryExpression : ICompiledStructure
    {
        public UnaryExpression(string op, ICompiledStructure operand)
        {
            Op = op;
            Operand = operand;
        }

        public string Op { get; }
        public ICompiledStructure Operand { get; }

        public void Decompile(StoryDecompiler dc)
        {
            dc.Out(Op + " ");
            if (Operand is BinaryExpression)
            {
                dc.Out("(");
                Operand.Decompile(dc);
                dc.Out(")");
            }
            else
            {
                Operand.Decompile(dc);
            }
        }
    }

    class BinaryExpression : ICompiledStructure
    {
        public BinaryExpression(string op, ICompiledStructure operand1, ICompiledStructure operand2)
        {
            Op = op;
            Operand1 = operand1;
            Operand2 = operand2;
        }

        public string Op { get; }
        public ICompiledStructure Operand1 { get; }
        public ICompiledStructure Operand2 { get; }

        public void Decompile(StoryDecompiler dc)
        {
            if (Operand1 is BinaryExpression || Operand1 is UnaryExpression)
            {
                dc.Out("(");
            }
            Operand1.Decompile(dc);
            if (Operand1 is BinaryExpression || Operand1 is UnaryExpression)
            {
                dc.Out(")");
            }
            dc.Out(" " + Op + " ");
            if (Operand2 is BinaryExpression || Operand2 is UnaryExpression)
            {
                dc.Out("(");
            }
            Operand2.Decompile(dc);
            if (Operand2 is BinaryExpression || Operand2 is UnaryExpression)
            {
                dc.Out(")");
            }
        }
    }

    class FunctionCall : ICompiledStructure
    {
        public FunctionCall(string name, List<ICompiledStructure> operands)
        {
            Name = name;
            Operands = operands;
        }

        public string Name { get; }
        public List<ICompiledStructure> Operands { get; }

        public void Decompile(StoryDecompiler dc)
        {
            dc.Out(Name + "(");
            for (int i=0; i < Operands.Count; i++)
            {
                if (i > 0)
                {
                    dc.Out(", ");
                }
                Operands[i].Decompile(dc);
            }
            dc.Out(")");
        }
    }

    class EmbeddedExpression : ICompiledStructure
    {
        private readonly ICompiledStructure expression;

        public EmbeddedExpression(ICompiledStructure expression)
        {
            this.expression = expression;
        }

        public void Decompile(StoryDecompiler dc)
        {
            dc.Out("{");
            expression.Decompile(dc);
            dc.Out("}");
        }
    }

    class StatementExpression : ICompiledStructure
    {
        private readonly ICompiledStructure expression;

        public StatementExpression(ICompiledStructure expression)
        {
            this.expression = expression;
        }

        public void Decompile(StoryDecompiler dc)
        {
            dc.EnsureNewLine();
            dc.Out("~ ");
            expression.Decompile(dc);
        }
    }

    class VariableAssignmentExpression : ICompiledStructure
    {
        private readonly string variableName;
        private readonly ICompiledStructure initializer;
        private readonly bool global;
        private readonly bool isDeclaration;

        public VariableAssignmentExpression(string variableName, ICompiledStructure initializer,
            bool global, bool isDeclaration)
        {
            this.variableName = variableName;
            this.initializer = initializer;
            this.global = global;
            this.isDeclaration = isDeclaration;
        }

        public void Decompile(StoryDecompiler dc)
        {
            if (isDeclaration)
            {
                dc.Out(global ? "VAR " : "~ temp ");
            }
            else {
                dc.Out("~ ");
            }
            dc.Out(variableName + " = ");
            initializer.Decompile(dc);
            dc.Out("\n");
        }
    }

    class ChoiceData : ICompiledStructure
    {
        public ChoiceData(ChoicePoint choice)
        {
            Choice = choice;
        }

        public CompiledContainer StartContent { get; set; }
        public CompiledContainer ChoiceOnlyContent { get; set; }
        public ICompiledStructure Condition { get; set; }
        public CompiledContainer InnerContent { get; set; }
        public ChoicePoint Choice { get; }

        public void Decompile(StoryDecompiler dc)
        {
            for (int i = 0; i < dc.ChoiceNesting; i++)
            {
                dc.Out("* ");
            }
            dc.Out(Choice.onceOnly ? "* " : "+ ");
            if (Condition != null)
            {
                dc.Out("{ ");
                Condition.Decompile(dc);
                dc.Out(" } ");
            }
            if (StartContent != null)
            {
                StartContent.Decompile(dc);
            }
            if (ChoiceOnlyContent != null)
            {
                dc.Out("[");
                ChoiceOnlyContent.Decompile(dc);
                dc.Out("]");
            }
            if (InnerContent != null)
            {
                dc.ChoiceNesting++;
                InnerContent.Decompile(dc);
                dc.ChoiceNesting--;
            }
        }
    }

    class WeaveData : ICompiledStructure
    {
        public WeaveData(List<ChoiceData> choices, CompiledContainer gatherContent, Path outerGatherPath)
        {
            Choices = choices;
            GatherContent = gatherContent;
            OuterGatherPath = outerGatherPath;
        }

        public List<ChoiceData> Choices { get; }
        public CompiledContainer GatherContent { get; set; }
        public Path OuterGatherPath { get; }

        public void Decompile(StoryDecompiler dc)
        {
            foreach (var choice in Choices)
            {
                choice.Decompile(dc);
            }
            if (GatherContent != null && !IsGeneratedGather(GatherContent))
            {
                for (int i = 0; i < dc.ChoiceNesting; i++)
                {
                    dc.Out("- ");
                }
                dc.Out("- ");
                GatherContent.Decompile(dc);
            }
        }

        private bool IsGeneratedGather(CompiledContainer containerRange)
        {
            return containerRange.Count == 1 &&
                containerRange.At(0) is CompiledObject obj &&
                obj.Obj.IsControlCommand(ControlCommand.CommandType.Done);
        }
    }

    class SequenceData : ICompiledStructure
    {
        public SequenceData(List<CompiledContainer> branches, bool cycle, bool shuffle) 
        {
            Branches = branches;
            Cycle = cycle;
            Shuffle = shuffle;
        }

        public List<CompiledContainer> Branches { get; }
        public bool Cycle { get; }
        public bool Shuffle { get; }

        public void Decompile(StoryDecompiler dc)
        {
            var branches = Branches;
            dc.Out("{");
            if (Cycle)
            {
                dc.Out("&");
            }
            else if (Shuffle)
            {
                dc.Out("~");
            }
            else if (branches.Count > 2 && branches[branches.Count - 1].Count == 0)
            {
                dc.Out("!");
                branches = branches.GetRange(0, branches.Count - 1);
            }
            var first = true;
            foreach (var branch in branches)
            {
                if (!first)
                {
                    dc.Out("|");
                }
                first = false;
                branch.Decompile(dc);
            }
            dc.Out("}");
        }
    }

    class ConditionalData : ICompiledStructure
    {
        public ConditionalData(List<ICompiledStructure> conditions, List<CompiledContainer> branches,
            ICompiledStructure discriminator = null)
        {
            Conditions = conditions;
            Branches = branches;
            Discriminator = discriminator;
        }

        public List<ICompiledStructure> Conditions { get; }
        public List<CompiledContainer> Branches { get; }
        public ICompiledStructure Discriminator { get; }

        public void Decompile(StoryDecompiler dc)
        {
            dc.Out("{");
            if (Discriminator != null)
            {
                Discriminator.Decompile(dc);
                dc.Out(":\n");
            }
            for (int i = 0; i < Conditions.Count; i++)
            {
                if (Conditions.Count > 1)
                {
                    dc.Out("- ");
                }
                Conditions[i].Decompile(dc);
                dc.Out(":");
                Branches[i].Decompile(dc);
            }
            if (Branches.Count > Conditions.Count) 
            {
                if (IsMultiline())
                {
                    dc.Out("- else:");
                }
                else
                {
                    dc.Out("|");
                }
                Branches[Branches.Count - 1].Decompile(dc);
            }
            dc.Out("}");
        }

        private bool IsMultiline()
        {
            if (Conditions.Count > 1) return true;
            foreach (ICompiledStructure element in Branches)
            {
                if (IsMultilineElement(element))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsMultilineElement(ICompiledStructure element)
        {
            if (element is VariableAssignmentExpression || element is StatementExpression)
            {
                return true;
            }
            if (element is CompiledContainer container)
            {
                foreach (ICompiledStructure child in container)
                {
                    if (IsMultilineElement(child))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    class ContainerCursor
    {
        public Container Container { get; private set; }
        public int Index { get; private set; }
        private readonly List<Ink.Runtime.Object> content;

        public ContainerCursor(Container container, int startIndex = 0)
        {
            this.Container = container;
            this.content = container.content;
            this.Index = startIndex;
        }

        public bool AtEnd()
        {
            return Index >= content.Count;
        }

         public Ink.Runtime.Object Current => content[Index];

        public Ink.Runtime.Object TakeNext()
        {
            return content[Index++];
        }

        public bool SkipControlCommand(ControlCommand.CommandType commandType)
        {
            if (Index < content.Count &&
                content[Index] is ControlCommand command &&
                command.commandType == commandType)
            {
                Index++;
                return true;
            }
            return false;
        }

        public bool SkipToLabel(string label)
        {
            while (Index < content.Count)
            {
                if (content[Index] is Container child && child.name == label)
                {
                    Index++;
                    return true;
                }
                Index++;
            }
            return false;
        }

        public bool SkipToVarAssign()
        {
            for (int targetIndex = Index; targetIndex < content.Count; targetIndex++)
            {
                if (content[targetIndex] is VariableAssignment)
                {
                    Index = targetIndex + 1;
                    return true;
                }
            }
            return false;
        }

        public bool SkipToControlCommand(ControlCommand.CommandType commandType)
        {
            while (Index < content.Count)
            {
                if (content[Index] is ControlCommand command && command.commandType == commandType)
                {
                    Index++;
                    return true;
                }
                Index++;
            }
            return false;
        }
    }

    public static class InkExtensions
    {
        public static bool IsControlCommand(this Ink.Runtime.Object obj, ControlCommand.CommandType type)
        {
            return obj is ControlCommand cmd && cmd.commandType == type;
        }

        public static bool IsDone(this Container container)
        {
            return container.content.Count > 0 &&
                container.content[0].IsControlCommand(ControlCommand.CommandType.Done);
        }
    }
}
