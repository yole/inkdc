using System;
using System.Collections.Generic;
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

        public string DecompileRoot()
        {
            Container mainContentContainer = Story.mainContentContainer;
            var result = DecompileContainer(mainContentContainer);
            if (mainContentContainer.namedOnlyContent != null)
            {
                foreach (string name in mainContentContainer.namedOnlyContent.Keys)
                {
                    var namedContent = mainContentContainer.namedOnlyContent[name];
                    if (namedContent is Container namedContainer)
                    {
                        result += "== " + name + " ==\n";
                        result += DecompileKnot(namedContainer);
                    }
                }
            }
            
            return result;
        }

        string DecompileKnot(Container container)
        {
            var result = DecompileContainer(container);

            if (container.namedOnlyContent != null)
            {
                foreach (string name in container.namedOnlyContent.Keys)
                {
                    var namedContent = container.namedOnlyContent[name];
                    if (namedContent is Container namedContainer)
                    {
                        result += "= " + name + "\n";
                        result += DecompileContainer(namedContainer);
                    }
                }
            }
            return result;
        }

        string DecompileContainer(Container container)
        {
            var choices = AnalyzeChoices(container);
            if (choices.Count > 0)
            {
                var result = "";
                foreach (ChoiceData choice in choices)
                {
                    result += DecompileChoice(choice);
                }
                return result;
            }

            return DecompileList(container.content);
        }

        private string DecompileList(List<Ink.Runtime.Object> content)
        {
            var result = "";
            foreach (var item in content)
            {
                if (item is Container childContainer)
                {
                    result += DecompileContainer(childContainer);
                }
                else if (item is StringValue stringValue)
                {
                    result += stringValue.value;
                }
                else if (item is Divert divert && !IsGeneratedDivert(divert))
                {
                    result += "-> " + divert.targetPathString + "\n";
                }
            }
            return result;
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

        string DecompileChoice(ChoiceData choiceData)
        {
            var result = choiceData.Choice.onceOnly ? "* " : "+ ";
            if (choiceData.StartContent != null)
            {
                result += DecompileList(choiceData.StartContent);
            }
            if (choiceData.ChoiceOnlyContent != null)
            {
                result += "[" + DecompileList(choiceData.ChoiceOnlyContent) + "]";
            }
            if (choiceData.InnerContent != null)
            {
                result += DecompileList(choiceData.InnerContent);
            }
            return result;
        }

        List<ChoiceData> AnalyzeChoices(Container container)
        {
            var result = new List<ChoiceData>();
            var content = container.content;

            // normally each choice is generated into its own container
            // however, containers may be combined by flattening, which will lead
            // to multiple choices in one container

            var choiceStart = 0;
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
    }


    class ChoiceData
    {
        public ChoiceData(ChoicePoint choice)
        {
            Choice = choice;
        }

        public List<Ink.Runtime.Object> StartContent { get; set; }
        public List<Ink.Runtime.Object> ChoiceOnlyContent { get; set; }
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
            return content.GetRange(start, index - start);
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
