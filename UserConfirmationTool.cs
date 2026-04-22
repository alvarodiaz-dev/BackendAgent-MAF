using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace BasicAgent
{
    /// <summary>
    /// Tool that allows skills to request user confirmation during execution.
    /// Skills can call this to pause and ask the user for approval to continue.
    /// </summary>
    internal static class UserConfirmationTool
    {
        /// <summary>
        /// Requests user confirmation for continuing with a specific action.
        /// This is called by skills that need human approval to proceed.
        /// </summary>
        [Description("Request user confirmation to proceed with a specific action or phase. Returns 'yes' if approved, 'no' if rejected.")]
        public static async Task<string> RequestUserConfirmation(
            [Description("The action or phase that requires confirmation")] string action,
            [Description("Optional detailed description of what will happen if confirmed")] string? details = null)
        {
            Console.WriteLine("\n" + new string('═', 70));
            Console.WriteLine("[Skill → User] CONFIRMATION REQUIRED");
            Console.WriteLine(new string('═', 70));

            Console.WriteLine($"\nAction: {action}");

            if (!string.IsNullOrWhiteSpace(details))
            {
                Console.WriteLine($"\nDetails:\n{details}");
            }

            Console.WriteLine("\n[Options]");
            Console.WriteLine("  [Y/y] - Approve and continue");
            Console.WriteLine("  [N/n] - Reject and stop");

            while (true)
            {
                Console.Write("\nYour confirmation: ");
                var input = Console.ReadLine()?.ToLower().Trim();

                if (input == "y")
                {
                    Console.WriteLine("✓ Approved. Continuing execution...\n");
                    return "yes";
                }
                else if (input == "n")
                {
                    Console.WriteLine("✗ Rejected. Stopping execution.\n");
                    return "no";
                }
                else
                {
                    Console.WriteLine("Invalid input. Please enter Y or N.");
                }
            }
        }

        /// <summary>
        /// Requests user confirmation with a yes/no question.
        /// </summary>
        [Description("Ask user a yes/no question and return their response.")]
        public static async Task<string> AskUserYesNo(
            [Description("The question to ask the user")] string question)
        {
            Console.WriteLine("\n" + new string('─', 70));
            Console.WriteLine("[Skill → User] REQUIRES YOUR INPUT");
            Console.WriteLine(new string('─', 70));

            Console.WriteLine($"\n{question}");
            Console.WriteLine("\n[Y/y] Yes    [N/n] No");

            while (true)
            {
                Console.Write("\nYour answer: ");
                var input = Console.ReadLine()?.ToLower().Trim();

                if (input == "y")
                {
                    Console.WriteLine("✓ Response: Yes\n");
                    return "yes";
                }
                else if (input == "n")
                {
                    Console.WriteLine("✓ Response: No\n");
                    return "no";
                }
                else
                {
                    Console.WriteLine("Invalid input. Please enter Y or N.");
                }
            }
        }

        /// <summary>
        /// Displays a notification to the user.
        /// Skills can use this to inform about important events.
        /// </summary>
        [Description("Display a notification message to the user.")]
        public static async Task<string> NotifyUser(
            [Description("The notification message to display")] string message,
            [Description("Type of notification: 'info', 'warning', or 'success'")] string notificationType = "info")
        {
            var prefix = (notificationType ?? "info").ToLower() switch
            {
                "warning" => "⚠",
                "success" => "✓",
                _ => "ℹ"
            };

            Console.WriteLine($"\n[{prefix} {(notificationType ?? "info").ToUpper()}] {message}\n");
            return "notified";
        }
    }
}
