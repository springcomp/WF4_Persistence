using System;
using System.Activities;

namespace Workflow
{
    public sealed class BookmarkActivity : NativeActivity
    {
        protected override void Execute(NativeActivityContext context)
        {
            const string bookmarkName = "bookmark";

            Console.WriteLine($"LOG: creating bookmark named: \"{bookmarkName}\".");
            context.CreateBookmark(bookmarkName, OnResumeBookmark);
        }

        protected override bool CanInduceIdle
            => true;

        private void OnResumeBookmark(NativeActivityContext context, Bookmark bookmark, object value)
        {
        }
    }
}