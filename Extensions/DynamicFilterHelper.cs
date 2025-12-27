using Newtonsoft.Json.Serialization;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

public static class DynamicFilterHelper
{
    public static IQueryable<T> ApplyFilters<T, F>(IQueryable<T> query, F filterModel, bool ignoreCase = true)
    {
        if (filterModel == null) return query;

        var param = Expression.Parameter(typeof(T), "x");
        Expression predicate = null;
        var qryPropNames = typeof(T).GetProperties().Select(p=>p.Name);
        foreach (var prop in typeof(F).GetProperties())
        {
            string propName = prop.Name
            .Replace("From", "")
            .Replace("To", "")
            .Replace("StartsWith", "")
            .Replace("EndsWith", "")
            .Replace("Equals", ""); 
            //بعضی از prop های فیلتر مثل Take و Skip ممکن است جزئی ار prop های کوئری نباشند
            if(!qryPropNames.Contains(propName))
            {
                continue;
            }
            var value = prop.GetValue(filterModel);
            if (value == null) continue;

            var exp = BuildExpression(param, prop.Name, value, ignoreCase);
            if (exp != null)
                predicate = predicate == null ? exp : Expression.AndAlso(predicate, exp);
        }

        return predicate == null
            ? query
            : query.Where(Expression.Lambda<Func<T, bool>>(predicate, param));
    }

    private static Expression BuildExpression(
        ParameterExpression param,
        string propName,
        object value,
        bool ignoreCase)
    {
        // پیدا کردن نام ستون واقعی (حذف پسوندهای مخصوص فیلتر)
        string targetName = propName
            .Replace("From", "")
            .Replace("To", "")
            .Replace("StartsWith", "")
            .Replace("EndsWith", "")
            .Replace("Equals", "");

        var column = Expression.Property(param, targetName);
        var columnType = column.Type;

        // انواع عددی/تاریخی/بولی
        if (IsNumericOrDateOrBool(value))
        {
            if (propName.EndsWith("From"))
            {
                var rhs = AsEfParameter(value, columnType);
                return Expression.GreaterThanOrEqual(column, rhs);
            }
            if (propName.EndsWith("To"))
            {
                if (value is DateTime dt) value = dt.AddDays(1);
                var rhs = AsEfParameter(value, columnType);
                return Expression.LessThanOrEqual(column, rhs);
            }
            return Expression.Equal(column, AsEfParameter(value, columnType));
        }

        // رشته‌ای
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            Expression left = column;
            Expression right = AsEfParameter(s, typeof(string));

            if (ignoreCase)
            {
                var toLower = typeof(string).GetMethod("ToLower", Type.EmptyTypes);
                left = Expression.Call(left, toLower);
                right = Expression.Call(right, toLower);
            }

            if (propName.EndsWith("Equals"))
                return Expression.Equal(left, right);

            MethodInfo mi =
                propName.EndsWith("StartsWith") ? typeof(string).GetMethod("StartsWith", new[] { typeof(string) }) :
                propName.EndsWith("EndsWith") ? typeof(string).GetMethod("EndsWith", new[] { typeof(string) }) :
                                                  typeof(string).GetMethod("Contains", new[] { typeof(string) });

            return Expression.Call(left, mi, right);
        }

        return null;
    }

    /// <summary>
    /// برای اینکه EF6 مقدار رو به صورت پارامتر SQL بفرسته نه inline
    /// </summary>
    private static Expression AsEfParameter(object value, Type targetType)
    {
        var holder = new { Value = value };
        Expression access = Expression.Property(Expression.Constant(holder), "Value");
        if (access.Type != targetType)
            access = Expression.Convert(access, targetType);
        return access;
    }

    private static bool IsNumericOrDateOrBool(object value)
    {
        return value is int || value is int? ||
               value is long || value is long? ||
               value is double || value is double? ||
               value is decimal || value is decimal? ||
               value is DateTime || value is DateTime? ||
               value is bool || value is bool?;
    }
}
