using System;
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
            AnalyzeContainer(mainContentContainer).Decompile(this);
            if (mainContentContainer.namedOnlyContent != null)
            {
                foreach (string name in mainContentContainer.namedOnlyContent.Keys)
                {
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
                var weave = AnalyzeWeave(container, index);
                if (weave != null)
                {
                    result.Add(weave);
                    index = weave.EndIndex;
                    continue;
                }
                if (container.content[index].IsControlCommand(ControlCommand.CommandType.EvalStart))
                {
                    var choice = AnalyzeChoice(container, index);
                    if (choice != null)
                    {
                        result.Add(choice);
                        index = choice.EndIndex;
                        continue;
                    }
                    var sequence = AnalyzeSequence(container, index);
                    if (sequence != null)
                    {
                        result.Add(sequence);
                        index = sequence.EndIndex;
                        continue;
                    }
                    var conditional = AnalyzeConditional(container, index);
                    if (conditional != null)
                    {
                        result.Add(conditional);
                        index = conditional.EndIndex;
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

        ChoiceData AnalyzeChoice(Container container, int index)
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
            ChoiceData choiceData = new(choicePoint, choicePointIndex + 1);
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

            return choiceData;
        }

        WeaveData AnalyzeWeave(Container container, int index)
        {
            List<ChoiceData> choices = new();
            while (true)
            {
                if (index < container.content.Count &&
                    container.content [index] is Container childContainer)
                {
                    ChoiceData choice = AnalyzeChoice(childContainer, 0);
                    if (choice == null) break;
                    choices.Add(choice);
                    index++;
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
            return new WeaveData(choices, gatherContent, outerGatherPath, index);
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

        SequenceData AnalyzeSequence(Container container, int index)
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
            return new SequenceData(cursor.Index, branches, cycle, shuffle);
        }

        private static bool IsSequenceBranchDivert(Divert divert)
        {
            Pointer targetPointer = divert.targetPointer;
            if (targetPointer.container == null) return false;
            var name = targetPointer.container.name;
            return name != null && name.StartsWith("s") && targetPointer.index == 0;
        }

        ConditionalData AnalyzeConditional(Container container, int index)
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
                return new ConditionalData(AnalyzeExpression(container, conditionStart, conditionEnd),
                    branches, cursor.Index);
            }

            return null;
        }

        public ICompiledStructure AnalyzeExpression(Container container, int startIndex, int endIndex)
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
                    else
                    {
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
                        var operand = stack.Pop();
                        stack.Push(new FunctionCall("TURNS_SINCE", new() { operand }));
                    }
                    else
                    {
                        throw new NotSupportedException("Don't know how to decompile " + obj);
                    }
                }
                else
                {
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

        static HashSet<string> _binaryOperators = new()
        {
            "+", "-", "/", "*", "%", "==", "<", ">", ">=", "<=", "!=", "&&", "||"
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
                if (controlCommand.commandType != ControlCommand.CommandType.Done)
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

    public class CompiledContainer : ICompiledStructure
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

    class ChoiceData : ICompiledStructure
    {
        public ChoiceData(ChoicePoint choice, int endIndex)
        {
            Choice = choice;
            EndIndex = endIndex;
        }

        public CompiledContainer StartContent { get; set; }
        public CompiledContainer ChoiceOnlyContent { get; set; }
        public ICompiledStructure Condition { get; set; }
        public CompiledContainer InnerContent { get; set; }
        public ChoicePoint Choice { get; }
        public int EndIndex { get; }

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
        public WeaveData(List<ChoiceData> choices, CompiledContainer gatherContent, Path outerGatherPath, int endIndex)
        {
            Choices = choices;
            GatherContent = gatherContent;
            OuterGatherPath = outerGatherPath;
            EndIndex = endIndex;
        }

        public List<ChoiceData> Choices { get; }
        public CompiledContainer GatherContent { get; set; }
        public Path OuterGatherPath { get; }
        public int EndIndex { get; }

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
        public SequenceData(int endIndex, List<CompiledContainer> branches, bool cycle, bool shuffle) 
        {
            EndIndex = endIndex;
            Branches = branches;
            Cycle = cycle;
            Shuffle = shuffle;
        }

        public List<CompiledContainer> Branches { get; }
        public bool Cycle { get; }
        public bool Shuffle { get; }
        public int EndIndex { get; }

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
        public ConditionalData(ICompiledStructure condition, List<CompiledContainer> branches, int endIndex)
        {
            Condition = condition;
            Branches = branches;
            EndIndex = endIndex;
        }

        public ICompiledStructure Condition { get; }
        public List<CompiledContainer> Branches { get; }
        public int EndIndex { get; }

        public void Decompile(StoryDecompiler dc)
        {
            dc.Out("{");
            Condition.Decompile(dc);
            dc.Out(":");
            Branches[0].Decompile(dc);
            if (Branches.Count == 2)
            {
                dc.Out("|");
                Branches[1].Decompile(dc);
            }
            else if (Branches.Count > 2)
            {
                throw new NotSupportedException("Don't know how to decompile >2 branches");
            }
            dc.Out("}");
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
