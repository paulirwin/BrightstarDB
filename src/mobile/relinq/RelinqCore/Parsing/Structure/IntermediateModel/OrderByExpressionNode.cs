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
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Remotion.Linq.Clauses;
using Remotion.Linq.Utilities;

namespace Remotion.Linq.Parsing.Structure.IntermediateModel
{
  /// <summary>
  /// Represents a <see cref="MethodCallExpression"/> for 
  /// <see cref="Queryable.OrderBy{TSource,TKey}(System.Linq.IQueryable{TSource},System.Linq.Expressions.Expression{System.Func{TSource,TKey}})"/>.
  /// It is generated by <see cref="ExpressionTreeParser"/> when an <see cref="Expression"/> tree is parsed.
  /// </summary>
  public class OrderByExpressionNode : MethodCallExpressionNodeBase
  {
    public static readonly MethodInfo[] SupportedMethods = new[]
                                                           {
                                                               GetSupportedMethod (() => Queryable.OrderBy<object, object> (null, null)),
                                                               GetSupportedMethod (() => Enumerable.OrderBy<object, object> (null, null)),
                                                           };

    private readonly ResolvedExpressionCache<Expression> _cachedSelector;

    public OrderByExpressionNode (MethodCallExpressionParseInfo parseInfo, LambdaExpression keySelector)
        : base (parseInfo)
    {
      ArgumentUtility.CheckNotNull ("keySelector", keySelector);

      if (keySelector.Parameters.Count != 1)
        throw new ArgumentException ("KeySelector must have exactly one parameter.", "keySelector");

      KeySelector = keySelector;
      _cachedSelector = new ResolvedExpressionCache<Expression> (this);
    }

    public LambdaExpression KeySelector { get; private set; }

    public Expression GetResolvedKeySelector (ClauseGenerationContext clauseGenerationContext)
    {
      return _cachedSelector.GetOrCreate (r => r.GetResolvedExpression (KeySelector.Body, KeySelector.Parameters[0], clauseGenerationContext));
    }

    public override Expression Resolve (
        ParameterExpression inputParameter, Expression expressionToBeResolved, ClauseGenerationContext clauseGenerationContext)
    {
      ArgumentUtility.CheckNotNull ("inputParameter", inputParameter);
      ArgumentUtility.CheckNotNull ("expressionToBeResolved", expressionToBeResolved);

      // this simply streams its input data to the output without modifying its structure, so we resolve by passing on the data to the previous node
      return Source.Resolve (inputParameter, expressionToBeResolved, clauseGenerationContext);
    }

    protected override QueryModel ApplyNodeSpecificSemantics (QueryModel queryModel, ClauseGenerationContext clauseGenerationContext)
    {
      ArgumentUtility.CheckNotNull ("queryModel", queryModel);

      var clause = new OrderByClause();
      clause.Orderings.Add (new Ordering (GetResolvedKeySelector (clauseGenerationContext), OrderingDirection.Asc));
      queryModel.BodyClauses.Add (clause);

      return queryModel;
    }
  }
}
