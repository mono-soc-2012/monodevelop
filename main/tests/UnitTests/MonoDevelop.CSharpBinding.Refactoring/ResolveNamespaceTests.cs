// 
// ResolveNamespaceTests.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2012 Xamarin Inc. (http://xamarin.com)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using NUnit.Framework;
using MonoDevelop.Projects;
using MonoDevelop.Core;
using MonoDevelop.CSharpBinding.Refactoring;
using MonoDevelop.CSharpBinding.Tests;
using MonoDevelop.CSharp.Refactoring.ExtractMethod;
using System.Collections.Generic;
using MonoDevelop.CSharpBinding;
using System.Text;
using MonoDevelop.Ide.Gui;
using MonoDevelop.CSharp.Resolver;
using MonoDevelop.CSharp.Parser;
using Mono.TextEditor;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.CSharp;
using MonoDevelop.CSharp;
using MonoDevelop.CSharp.Completion;

namespace MonoDevelop.CSharpBinding.Refactoring
{
	[TestFixture()]
	public class ResolveNamespaceTests : UnitTests.TestBase
	{
		static Document Setup (string input)
		{
			TestWorkbenchWindow tww = new TestWorkbenchWindow ();
			var content = new TestViewContent ();
			tww.ViewContent = content;
			content.ContentName = "a.cs";
			content.GetTextEditorData ().Document.MimeType = "text/x-csharp";

			Document doc = new Document (tww);

			var text = input;
			int endPos = text.IndexOf ('$');
			if (endPos >= 0)
				text = text.Substring (0, endPos) + text.Substring (endPos + 1);

			content.Text = text;
			content.CursorPosition = System.Math.Max (0, endPos);

			var compExt = new CSharpCompletionTextEditorExtension ();
			compExt.Initialize (doc);
			content.Contents.Add (compExt);

			doc.UpdateParseDocument ();
			return doc;
		}

		HashSet<string> GetResult (string input)
		{
			var doc = Setup (input);
			var location = doc.Editor.Caret.Location;
			ResolveResult resolveResult;
			AstNode node;
			doc.TryResolveAt (location, out resolveResult, out node);
			return ResolveCommandHandler.GetPossibleNamespaces (doc, resolveResult);
		}

		[Test ()]
		public void TestObjectCreationType ()
		{
			var result = GetResult (@"class Test {
	void MyMethod ()
	{
		var list = new $List<string> ();
	}
}");
			Assert.IsTrue (result.Contains ("System.Collections.Generic"));
		}

		[Test ()]
		public void TestLocalVariableType ()
		{
			var result = GetResult (@"class Test {
	void MyMethod ()
	{
		$List<string> list;
	}
}");
			Assert.IsTrue (result.Contains ("System.Collections.Generic"));
		}

		[Test ()]
		public void TestParameterType ()
		{
			var result = GetResult (@"class Test {
	void MyMethod ($List<string> list)
	{
	}
}");
			Assert.IsTrue (result.Contains ("System.Collections.Generic"));
		}
		
		[Test ()]
		public void TestFieldType ()
		{
			var result = GetResult (@"class Test {
	$List<string> list;
}");
			Assert.IsTrue (result.Contains ("System.Collections.Generic"));
		}
		
		
		[Test ()]
		public void TestBaseType ()
		{
			var result = GetResult (@"class Test : $List<string> {}");
			Assert.IsTrue (result.Contains ("System.Collections.Generic"));
		}
		

		[Test ()]
		public void TestLocalVariableValid ()
		{
			var result = GetResult (@"using System.Collections.Generic;
class Test {
	void MyMethod ()
	{
		$List<string> list;
	}
}");
			Assert.AreEqual (0, result.Count);
		}

		[Test ()]
		public void TestAttributeCase1 ()
		{
			var result = GetResult (@"
[$Obsolete]
class Test {
}");
			Assert.IsTrue (result.Contains ("System"));
		}
		
		[Test ()]
		public void TestAttributeCase2 ()
		{

			var result = GetResult (@"
[$SerializableAttribute]
class Test {
}");
			Assert.IsTrue (result.Contains ("System"));
		}

		[Test ()]
		public void TestAmbigiousResolveResult ()
		{

			var result = GetResult (@"namespace Foo { class Bar {} }
namespace Foo2 { class Bar {} }

namespace My
{
	using Foo;
	using Foo2;

	class Program
	{
		public static void Main ()
		{
			$Bar bar;
		}
	}
}");
			foreach (var a in result)
				Console.WriteLine (a);
			Assert.IsTrue (result.Contains ("Foo"));
			Assert.IsTrue (result.Contains ("Foo2"));
		}


		#region Bug 3453 - [New Resolver] "Resolve" doesn't show up from time
		[Test ()]
		public void TestBug3453Case1 ()
		{
			var result = GetResult (@"class Test {
	string GetName ()
	{
		var encoding = $Encoding
		return encoding.EncodingName;
	}
}");
			Assert.IsTrue (result.Contains ("System.Text"));
		}

		[Test ()]
		public void TestBug3453Case2 ()
		{
			var result = GetResult (@"class Test {
	string GetName ()
	{
		$Encoding
		return encoding.EncodingName;
	}
}");
			Assert.IsTrue (result.Contains ("System.Text"));
		}

		[Test ()]
		public void TestBug3453Case3 ()
		{
			var result = GetResult (@"class Test {
	string GetName ()
	{
		$Encoding.
	}
}");
			Assert.IsTrue (result.Contains ("System.Text"));
		}

		[Test ()]
		public void TestBug3453Case3WithGeneris ()
		{
			var result = GetResult (@"class Test {
	string GetName ()
	{
		$List<string>.
	}
}");
			Assert.IsTrue (result.Contains ("System.Collections.Generic"));
		}
		#endregion
	}
}
