﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Amnesia
{
	/// <summary>
	/// Implementation of IDisposible that invokes a delegate
	/// when the object is disposed.
	/// </summary>
	internal class UndoableAction : IDisposable
	{
		public static IDisposable Null = new UndoableAction(null);

		Action undo;

		public UndoableAction(Action undo)
		{
			this.undo = undo;
		}

		public void Dispose()
		{
			if (undo != null)
			{

				undo();
				undo = null;
			}
		}
	}
}
