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
            for (int i=0; i<container.content.Count; i++)
            {
                if (container.content[i].IsControlCommand(ControlCommand.CommandType.EvalStart))
                {
                    var choices = AnalyzeChoices(container, i);
                    if (choices.Count > 0)
                    {
                        foreach(ChoiceData choice in choices)
                        {
                            DecompileChoice(choice);
                        }
                        break;
                    }
                }
                DecompileObject(container.content[i]);
            }
        }

        private void DecompileList(List<Ink.Runtime.Object> content)
        {
            foreach (var item in content)
            {
                DecompileObject(item);
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
                Out(DecompileExpression(choiceData.Condition));
                Out(" } ");
            }
            if (choiceData.StartContent != null)
            {
                DecompileList(choiceData.StartContent);
            }
            if (choiceData.ChoiceOnlyContent != null)
            {
                Out("[");
                DecompileList(choiceData.ChoiceOnlyContent);
                Out("]");
            }
            if (choiceData.InnerContent != null)
            {
                DecompileList(choiceData.InnerContent);
            }
        }

        List<ChoiceData> AnalyzeChoices(Container container, int startIndex)
        {
            var result = new List<ChoiceData>();
            var content = container.content;

            // normally each choice is generated into its own container
            // however, containers may be combined by flattening, which will lead
            // to multiple choices in one container

            var choiceStart = startIndex;
            while (true)
            {
                var choicePointIndex = content.FindIndex(choiceStart, (x) => x is ChoicePoint);
                if (choicePointIndex < 0) break;

                ChoicePoint choicePoint = (ChoicePoint)content[choicePointIndex];
                ChoiceData choiceData = new(choicePoint);
                ContainerCursor cursor = new(container, choiceStart);
                cursor.SkipControlCommand(ControlCommand.CommandType.EvalStart);

                if (choicePoint.hasStartContent)
                {
                    Container startContentContainer = (Container)container.namedContent["s"];
                    // last element is divert to $r, need to skip it from decompilation
                    choiceData.StartContent = startContentContainer.content.GetRange(0, startContentContainer.content.Count - 1);
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

                result.Add(choiceData);
                choiceStart = choicePointIndex + 1;
            }

            return result;
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
                        stack.Push("not " + stack.Pop());
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
    }


    class ChoiceData
    {
        public ChoiceData(ChoicePoint choice)
        {
            Choice = choice;
        }

        public List<Ink.Runtime.Object> StartContent { get; set; }
        public List<Ink.Runtime.Object> ChoiceOnlyContent { get; set; }
        public List<Ink.Runtime.Object> Condition { get; set; }
        public List<Ink.Runtime.Object> InnerContent { get; set; }
        public ChoicePoint Choice { get; }
    }

    class ContainerCursor
    {
        private readonly Container container;
        private readonly List<Ink.Runtime.Object> content;

        private int index;

        public ContainerCursor(Container container, int startIndex = 0)
        {
            this.container = container;
            this.content = container.content;
            this.index = startIndex;
        }

        public void SkipControlCommand(ControlCommand.CommandType commandType)
        {
            if (index < content.Count &&
                content[index] is ControlCommand command &&
                command.commandType == commandType)
            {
                index++;
            }
        }

        public bool SkipToLabel(string label)
        {
            while (index < content.Count)
            {
                if (content[index] is Container child && child.name == label)
                {
                    index++;
                    return true;
                }
                index++;
            }
            return false;
        }

        public List<Ink.Runtime.Object> SubListTo(ControlCommand.CommandType commandType)
        {
            var start = index;
            while (index < container.content.Count)
            {
                if (container.content[index] is ControlCommand command &&
                    command.commandType == commandType)
                {
                    break;
                }
                index++;
            }
            var result = content.GetRange(start, index - start);
            index++;
            return result;
        }

        public List<Ink.Runtime.Object> Tail()
        {
            return content.GetRange(index, content.Count - index);
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
