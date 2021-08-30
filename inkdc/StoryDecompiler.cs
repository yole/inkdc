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

        public string DecompileRoot()
        {
            Container mainContentContainer = Story.mainContentContainer;
            DecompileContainer(mainContentContainer);
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

        void Out(string text)
        {
            result.Append(text);
        }

        void DecompileKnot(Container container)
        {
            DecompileContainer(container);

            if (container.namedOnlyContent != null)
            {
                foreach (string name in container.namedOnlyContent.Keys)
                {
                    var namedContent = container.namedOnlyContent[name];
                    if (namedContent is Container namedContainer)
                    {
                        Out("= " + name + "\n");
                        DecompileContainer(namedContainer); 
                    }
                }
            }
        }

        void DecompileContainer(Container container)
        {
            DecompileContainerRange(new ContainerRange(container, 0, container.content.Count));
        }

        void DecompileContainerRange(ContainerRange containerRange)
        {
            Container container = containerRange.Container;
            int index = containerRange.StartIndex;
            while (index < containerRange.EndIndex)
            {
                if (container.content[index].IsControlCommand(ControlCommand.CommandType.EvalStart))
                {
                    var choice = AnalyzeChoice(container, index);
                    if (choice != null)
                    {
                        DecompileChoice(choice);
                        index = choice.EndIndex;
                        continue;
                    }
                    var sequence = AnalyzeSequence(container, index);
                    if (sequence != null)
                    {
                        DecompileSequence(sequence);
                        index = sequence.EndIndex;
                        continue;
                    }
                    var conditional = AnalyzeConditional(container, index);
                    if (conditional != null)
                    {
                        DecompileConditional(conditional);
                        index = conditional.EndIndex;
                        continue;
                    }
                }
                DecompileObject(container.content[index++]);
            }
        }

        private void DecompileObject(Ink.Runtime.Object item)
        {
            if (item is Container childContainer)
            {
                DecompileContainer(childContainer);
            }
            else if (item is StringValue stringValue)
            {
                Out(stringValue.value);
            }
            else if (item is Divert divert)
            {
                if (!IsGeneratedDivert(divert))
                {
                    Out("-> " + divert.targetPathString + "\n");
                }
            }
            else if (item is ControlCommand controlCommand)
            {
                if (controlCommand.commandType != ControlCommand.CommandType.Done)
                {
                    throw new NotSupportedException("Don't know how to decompile " + item);
                }
            }
            else
            {
                throw new NotSupportedException("Don't know how to decompile " + item);
            }
        }

        private bool IsGeneratedDivert(Divert divert)
        {
            SearchResult searchResult = Story.ContentAtPath(divert.targetPath);
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

        void DecompileChoice(ChoiceData choiceData)
        {
            Out(choiceData.Choice.onceOnly ? "* " : "+ ");
            if (choiceData.Condition != null)
            {
                Out("{ ");
                Out(DecompileExpression(choiceData.Condition.Elements));
                Out(" } ");
            }
            if (choiceData.StartContent != null)
            {
                DecompileContainerRange(choiceData.StartContent);
            }
            if (choiceData.ChoiceOnlyContent != null)
            {
                Out("[");
                DecompileContainerRange(choiceData.ChoiceOnlyContent);
                Out("]");
            }
            if (choiceData.InnerContent != null)
            {
                DecompileContainerRange(choiceData.InnerContent);
            }
        }

        ChoiceData AnalyzeChoice(Container container, int index)
        {
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
                choiceData.StartContent = new(startContentContainer, 0, startContentContainer.content.Count - 1);
                if (cursor.SkipToLabel("$r1"))
                {
                    cursor.SkipControlCommand(ControlCommand.CommandType.EndString);
                }
            }
            if (choicePoint.hasChoiceOnlyContent)
            {
                cursor.SkipControlCommand(ControlCommand.CommandType.BeginString);
                choiceData.ChoiceOnlyContent = cursor.SubListTo(ControlCommand.CommandType.EndString);
            }
            if (choicePoint.hasCondition)
            {
                choiceData.Condition = cursor.SubListTo(ControlCommand.CommandType.EvalEnd);
            }

            var target = Story.ContentAtPath(choicePoint.pathOnChoice).container;
            if (target != null)
            {
                ContainerCursor targetCursor = new(target);
                if (choicePoint.hasStartContent)
                {
                    targetCursor.SkipToLabel("$r2");
                }
                choiceData.InnerContent = targetCursor.Tail();
            }

            return choiceData;
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
            var branches = new List<ContainerRange>();
            while (!cursor.AtEnd() && !cursor.Current.IsControlCommand(ControlCommand.CommandType.NoOp))
            {
                if (cursor.Current is Divert divert && IsSequenceBranchDivert(divert))
                {
                    var branchContainer = divert.targetPointer.container;
                    if (branchContainer != null)
                    {
                        // first element is a PopGeneratedValue(), last is divert back
                        branches.Add(new ContainerRange(branchContainer, 1, branchContainer.content.Count - 1));
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

        void DecompileSequence(SequenceData sequence)
        {
            var branches = sequence.Branches;
            Out("{");
            if (sequence.Cycle)
            {
                Out("&");
            }
            else if (sequence.Shuffle)
            {
                Out("~");
            }
            else if (branches.Count > 2 && branches[branches.Count-1].Count == 0)
            {
                Out("!");
                branches = branches.GetRange(0, branches.Count - 1);
            }
            var first = true;
            foreach (var branch in branches)
            {
                if (!first)
                {
                    Out("|");
                }
                first = false;
                DecompileContainerRange(branch);
            }
            Out("}");
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
            List<ContainerRange> branches = new();
            while (cursor.Current is Container nestedContainer)
            {
                if (nestedContainer.content[0] is Divert divert)
                {
                    var branchContainer = divert.targetPointer.container;
                    // last element is divert to rejoin target
                    branches.Add(new ContainerRange(branchContainer, 0, branchContainer.content.Count - 1));
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
                return new ConditionalData(new ContainerRange(container, conditionStart, conditionEnd),
                    branches, cursor.Index);
            }

            return null;
        }

        void DecompileConditional(ConditionalData conditional)
        {
            Out("{");
            Out(DecompileExpression(conditional.Condition.Elements));
            Out(":");
            DecompileContainerRange(conditional.Branches[0]);
            if (conditional.Branches.Count == 2)
            {
                Out("|");
                DecompileContainerRange(conditional.Branches[1]);
            }
            else if (conditional.Branches.Count > 2)
            {
                throw new NotSupportedException("Don't know how to decompile >2 branches");
            }
            Out("}");
        }

        string DecompileExpression(List<Ink.Runtime.Object> expression)
        {
            Stack<String> stack = new();
            foreach (Ink.Runtime.Object obj in expression)
            {
                if (obj is VariableReference varRef)
                {
                    if (varRef.name != null)
                    {
                        stack.Push(varRef.name);
                    }
                    else
                    {
                        stack.Push(varRef.pathStringForCount);
                    }
                }
                else if (obj is NativeFunctionCall call)
                {
                    if (call.name == "!")
                    {
                        var operand = stack.Pop();
                        if (operand.Contains(" "))
                        {
                            stack.Push("not(" + operand + ")");
                        }
                        else
                        {
                            stack.Push("not " + operand);
                        }
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
                else if (obj is IntValue)
                {
                    stack.Push(obj.ToString());
                }
                else
                {
                    throw new NotSupportedException("Don't know how to decompile " + obj);
                }

            }
            return stack.Pop();
        }

        void BuildBinaryExpression(Stack<String> stack, string op)
        {
            var operand1 = stack.Pop();
            var operand2 = stack.Pop();
            if (operand1.Contains(" "))
            {
                operand1 = "(" + operand1 + ")";
            }
            if (operand2.Contains(" "))
            {
                operand2 = "(" + operand2 + ")";
            }
            stack.Push(operand2 + " " + op + " " + operand1);
        }

        static HashSet<string> _binaryOperators = new HashSet<string>
        {
            "+", "-", "/", "*", "%", "==", "<", ">", ">=", "<=", "!=", "&&", "||"
        };
    }

    class ChoiceData
    {
        public ChoiceData(ChoicePoint choice, int endIndex)
        {
            Choice = choice;
            EndIndex = endIndex;
        }

        public ContainerRange StartContent { get; set; }
        public ContainerRange ChoiceOnlyContent { get; set; }
        public ContainerRange Condition { get; set; }
        public ContainerRange InnerContent { get; set; }
        public ChoicePoint Choice { get; }
        public int EndIndex { get; }
    }

    class SequenceData
    {
        public SequenceData(int endIndex, List<ContainerRange> branches, bool cycle, bool shuffle) 
        {
            EndIndex = endIndex;
            Branches = branches;
            Cycle = cycle;
            Shuffle = shuffle;
        }

        public List<ContainerRange> Branches { get; }
        public bool Cycle { get; }
        public bool Shuffle { get; }
        public int EndIndex { get; }
    }

    class ConditionalData
    {
        public ConditionalData(ContainerRange condition, List<ContainerRange> branches, int endIndex)
        {
            Condition = condition;
            Branches = branches;
            EndIndex = endIndex;
        }

        public ContainerRange Condition { get; }
        public List<ContainerRange> Branches { get; }
        public int EndIndex { get; }
    }

    class ContainerRange
    {
        public ContainerRange(Container container, int startIndex, int endIndex)
        {
            Container = container;
            StartIndex = startIndex;
            EndIndex = endIndex;
        }

        public Container Container { get; }
        public int StartIndex { get; }
        public int EndIndex { get; }
        public int Count => EndIndex - StartIndex;
        public List<Ink.Runtime.Object> Elements =>
            Container.content.GetRange(StartIndex, Count);
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

        public ContainerRange SubListTo(ControlCommand.CommandType commandType)
        {
            var start = Index;
            while (Index < Container.content.Count)
            {
                if (Container.content[Index] is ControlCommand command &&
                    command.commandType == commandType)
                {
                    break;
                }
                Index++;
            }
            var result = new ContainerRange(Container, start, Index);
            Index++;
            return result;
        }

        public ContainerRange Tail()
        {
            return new(Container, Index, content.Count);
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
