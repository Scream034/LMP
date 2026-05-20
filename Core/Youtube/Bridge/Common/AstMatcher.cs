using Acornima;
using Acornima.Ast;
using LMP.Core.Logger;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Обеспечивает высокопроизводительное сопоставление узлов AST Acornima с шаблонами MatchTemplate.
/// </summary>
internal static class AstMatcher
{
    /// <summary>
    /// Рекурсивно проверяет соответствие узла AST Acornima заданному шаблону.
    /// </summary>
    public static bool Matches(Node? node, MatchTemplate? template)
    {
        if (template is null) return true;
        if (node is null) return false;

        Log.Trace($"[AstMatcher] Checking Match - Node: {node.GetType().Name}, Template: {template.NodeType?.Name}");

        if (template.NodeType is not null && !template.NodeType.IsAssignableFrom(node.GetType()))
        {
            Log.Trace($"  └─ Type mismatch: Node is {node.GetType().Name}, expected assignable to {template.NodeType.Name}");
            return false;
        }

        if (template.Name is not null)
        {
            if (node is not Identifier id || id.Name != template.Name)
            {
                Log.Trace($"  └─ Name mismatch: Node is Identifier with name '{(node as Identifier)?.Name ?? "N/A"}', expected '{template.Name}'");
                return false;
            }
        }

        if (template.Value is not null)
        {
            if (node is not Literal lit || !Equals(lit.Value, template.Value))
            {
                Log.Trace($"  └─ Value mismatch: Node is Literal with value '{(node as Literal)?.Value ?? "N/A"}', expected '{template.Value}'");
                return false;
            }
        }

        if (template.Operator is not null)
        {
            var opStr = node switch
            {
                AssignmentExpression ae => MapOperator(ae.Operator),
                BinaryExpression be => MapOperator(be.Operator),
                UpdateExpression ue => MapOperator(ue.Operator),
                UnaryExpression une => MapOperator(une.Operator),
                _ => null
            };

            if (opStr is null || opStr != template.Operator)
            {
                Log.Trace($"  └─ Operator mismatch: Node operator is '{opStr ?? "N/A"}', expected '{template.Operator}'");
                return false;
            }
        }

        if (template.Async.HasValue)
        {
            bool isAsync = node switch
            {
                FunctionDeclaration fd => fd.Async,
                FunctionExpression fe => fe.Async,
                ArrowFunctionExpression afe => afe.Async,
                _ => false
            };
            if (isAsync != template.Async.Value)
            {
                Log.Trace($"  └─ Async flag mismatch: Node is Async={isAsync}, expected={template.Async.Value}");
                return false;
            }
        }

        if (template.Computed.HasValue)
        {
            if (node is not MemberExpression me || me.Computed != template.Computed.Value)
            {
                Log.Trace($"  └─ Computed flag mismatch: Node is Computed={(node as MemberExpression)?.Computed}, expected={template.Computed.Value}");
                return false;
            }
        }

        if (template.Optional.HasValue)
        {
            bool opt = node switch
            {
                MemberExpression me => me.Optional,
                CallExpression ce => ce.Optional,
                _ => false
            };
            if (opt != template.Optional.Value)
            {
                Log.Trace($"  └─ Optional flag mismatch: Node is Optional={opt}, expected={template.Optional.Value}");
                return false;
            }
        }

        if (template.Left is not null)
        {
            Node? leftNode = node switch
            {
                AssignmentExpression ae => ae.Left,
                BinaryExpression be => be.Left,
                _ => null
            };
            if (!Matches(leftNode, template.Left)) return false;
        }

        if (template.Right is not null)
        {
            Node? rightNode = node switch
            {
                AssignmentExpression ae => ae.Right,
                BinaryExpression be => be.Right,
                _ => null
            };
            if (!Matches(rightNode, template.Right)) return false;
        }

        if (template.Object is not null)
        {
            if (node is not MemberExpression me || !Matches(me.Object, template.Object)) return false;
        }

        if (template.Property is not null)
        {
            if (node is not MemberExpression me || !Matches(me.Property, template.Property)) return false;
        }

        if (template.Expression is not null)
        {
            if (node is not ExpressionStatement es || !Matches(es.Expression, template.Expression)) return false;
        }

        if (template.Callee is not null)
        {
            if (node is not CallExpression ce || !Matches(ce.Callee, template.Callee)) return false;
        }

        if (template.Id is not null)
        {
            Node? idNode = node switch
            {
                FunctionDeclaration fd => fd.Id,
                ClassDeclaration cd => cd.Id,
                VariableDeclarator vd => vd.Id,
                _ => null
            };
            if (!Matches(idNode, template.Id)) return false;
        }

        if (template.Init is not null)
        {
            if (node is not VariableDeclarator vd || !Matches(vd.Init, template.Init)) return false;
        }

        if (template.Or is not null)
        {
            bool matchedAny = false;
            for (int i = 0; i < template.Or.Length; i++)
            {
                if (Matches(node, template.Or[i]))
                {
                    matchedAny = true;
                    break;
                }
            }
            if (!matchedAny)
            {
                Log.Trace("  └─ Or-condition failure: None of the sub-templates matched.");
                return false;
            }
        }

        if (template.AnyKey is not null)
        {
            bool anyKeyMatched = node switch
            {
                VariableDeclaration vd => MatchAnyKey(vd.Declarations, template.AnyKey),
                BlockStatement bs => MatchAnyKey(bs.Body, template.AnyKey),
                Acornima.Ast.Program p => MatchAnyKey(p.Body, template.AnyKey),
                CallExpression ce => MatchAnyKey(ce.Arguments, template.AnyKey),
                _ => MatchAnyKey(node.ChildNodes, template.AnyKey)
            };

            if (!anyKeyMatched)
            {
                Log.Trace("  └─ AnyKey failure: Not all nested criteria were matched.");
                return false;
            }
        }

        if (template.Arguments is not null)
        {
            if (node is not CallExpression ce || ce.Arguments.Count != template.Arguments.Length)
            {
                Log.Trace($"  └─ Arguments count mismatch: Node has {((node as CallExpression)?.Arguments.Count) ?? 0}, expected {template.Arguments.Length}");
                return false;
            }

            for (int i = 0; i < template.Arguments.Length; i++)
            {
                if (!Matches(ce.Arguments[i], template.Arguments[i])) return false;
            }
        }

        if (template.ExpectedArgumentsCount.HasValue)
        {
            if (node is not CallExpression ce || ce.Arguments.Count != template.ExpectedArgumentsCount.Value)
            {
                Log.Trace($"  └─ ExpectedArgumentsCount mismatch: Node has {((node as CallExpression)?.Arguments.Count) ?? 0}, expected {template.ExpectedArgumentsCount.Value}");
                return false;
            }
        }

        return true;
    }

    private static bool MatchAnyKey<T>(NodeList<T> collection, MatchTemplate[] templates) where T : Node
    {
        for (int i = 0; i < templates.Length; i++)
        {
            var t = templates[i];
            bool matchedAny = false;
            for (int j = 0; j < collection.Count; j++)
            {
                if (Matches(collection[j], t))
                {
                    matchedAny = true;
                    break;
                }
            }
            if (!matchedAny) return false;
        }
        return true;
    }

    private static bool MatchAnyKey(IReadOnlyList<Node> collection, MatchTemplate[] templates)
    {
        for (int i = 0; i < templates.Length; i++)
        {
            var t = templates[i];
            bool matchedAny = false;
            for (int j = 0; j < collection.Count; j++)
            {
                if (Matches(collection[j], t))
                {
                    matchedAny = true;
                    break;
                }
            }
            if (!matchedAny) return false;
        }
        return true;
    }

    private static bool MatchAnyKey(ChildNodes collection, MatchTemplate[] templates)
    {
        for (int i = 0; i < templates.Length; i++)
        {
            var t = templates[i];
            bool matchedAny = false;
            
            var enumerator = collection.GetEnumerator();
            try
            {
                while (enumerator.MoveNext())
                {
                    if (Matches(enumerator.Current, t))
                    {
                        matchedAny = true;
                        break;
                    }
                }
            }
            finally
            {
                enumerator.Dispose();
            }

            if (!matchedAny) return false;
        }
        return true;
    }

    private static string? MapOperator(Operator op) => op switch
    {
        Operator.Assignment => "=",
        Operator.AdditionAssignment => "+=",
        Operator.SubtractionAssignment => "-=",
        Operator.MultiplicationAssignment => "*=",
        Operator.DivisionAssignment => "/=",
        Operator.LogicalAnd => "&&",
        Operator.LogicalOr => "||",
        Operator.StrictEquality => "===",
        Operator.StrictInequality => "!==",
        Operator.Equality => "==",
        Operator.Inequality => "!=",
        Operator.Increment => "++",
        Operator.Decrement => "--",
        Operator.LogicalNot => "!",
        _ => null
    };
}