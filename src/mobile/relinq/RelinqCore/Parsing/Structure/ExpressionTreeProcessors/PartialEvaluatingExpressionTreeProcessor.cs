// This file is part of the re-linq project (relinq.codeplex.com)
// Copyright (C) 2005-2009 rubicon informationstechnologie gmbh, www.rubicon.eu
// 
// re-linq is free software; you can redistribute it and/or modify it under 
// the terms of the GNU Lesser General Public License as published by the 
// Free Software Foundation; either version 2.1 of the License, 
// or (at your option) any later version.
// 
// re-linq is distributed in the hope that it will be useful, 
// but WITHOUT ANY WARRANTY; without even the implied warranty of 
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the 
// GNU Lesser General Public License for more details.
// 
// You should have received a copy of the GNU Lesser General Public License
// along with re-linq; if not, see http://www.gnu.org/licenses.
// 
using System.Linq.Expressions;
using Remotion.Linq.Parsing.ExpressionTreeVisitors;
using Remotion.Linq.Utilities;

namespace Remotion.Linq.Parsing.Structure.ExpressionTreeProcessors
{
  /// <summary>
  /// Analyzes an <see cref="Expression"/> tree for sub-trees that are evaluatable in-memory, and evaluates those sub-trees.
  /// </summary>
  /// <remarks>
  /// The <see cref="PartialEvaluatingExpressionTreeProcessor"/> uses the <see cref="PartialEvaluatingExpressionTreeVisitor"/> for partial evaluation.
  /// It performs two visiting runs over the <see cref="Expression"/> tree.
  /// </remarks>
  public class PartialEvaluatingExpressionTreeProcessor : IExpressionTreeProcessor
  {
    public Expression Process (Expression expressionTree)
    {
      ArgumentUtility.CheckNotNull ("expressionTree", expressionTree);

      return PartialEvaluatingExpressionTreeVisitor.EvaluateIndependentSubtrees (expressionTree);
    }
  }
}