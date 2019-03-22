using System;
using System.Activities;

namespace Workflow
{
    public sealed class BookmarkActivity : NativeActivity
    {
        protected override void Execute(NativeActivityContext context)
        {
            var identity = context.GetExtension<IAmWhoSeeWhatIDidThere>();

            var appName = identity.WhoAmI.ToString();
            var bookmarkName = appName;
            
            Console.WriteLine($"{appName}: creating bookmark named: \"{bookmarkName}\".");
            context.CreateBookmark(bookmarkName, OnResumeBookmark);
        }

        protected override bool CanInduceIdle
            => true;

        private void OnResumeBookmark(NativeActivityContext context, Bookmark bookmark, object value)
        {
        }
    }
}