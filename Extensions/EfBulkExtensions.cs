using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Data.Entity.Core.Mapping;
using System.Data.Entity.Core.Metadata.Edm;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Contexts;
using System.ComponentModel.DataAnnotations;

namespace BackendUtils.EFDataService
{
    public static class EfBulkExtensions
    {
        private static readonly Dictionary<Type, EntityMapping> _entityMappingCache = new Dictionary<Type, EntityMapping>();
        private static readonly Dictionary<Type, ColumnMapping> _keyCache = new Dictionary<Type, ColumnMapping>();

        #region Bulk Insert
        public static void BulkInsert<T>(this DbContext context, IEnumerable<T> data)
        {
            //string connStr = context.Database.Connection.ConnectionString;
            //using (var conn = new SqlConnection(connStr))
            var conn = (SqlConnection) context.Database.Connection;
            {
                if (conn.State != ConnectionState.Open) conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    context.BulkInsert(data, transaction);
                    transaction.Commit();
                }
            }
        }

        public static void BulkInsert<T>(this DbContext context, IEnumerable<T> data, SqlTransaction transaction)
        {
            if (data == null || !data.Any()) return;

            var mapping = GetEntityMapping(context, typeof(T), out ColumnMapping keyCol);
            var table = new DataTable();
            foreach (var col in mapping.Columns)
                table.Columns.Add(col.DbColumnName, Nullable.GetUnderlyingType(col.PropertyType) ?? col.PropertyType);

            foreach (var item in data)
            {
                var values = mapping.Columns.Select(c => c.Property.GetValue(item) ?? DBNull.Value).ToArray();
                table.Rows.Add(values);
            }

            using (var bulkCopy = new SqlBulkCopy(transaction.Connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = $"[{mapping.Schema}].[{mapping.TableName}]";
                bulkCopy.BatchSize = 5000;
                bulkCopy.BulkCopyTimeout = 0;
                foreach (var col in mapping.Columns)
                    bulkCopy.ColumnMappings.Add(col.DbColumnName, col.DbColumnName);

                bulkCopy.WriteToServer(table);
            };


        }
        public static void BulkInsertWithOutputIds<T>(this DbContext context, IList<T> data, SqlTransaction transaction = null)
        {
            if (data == null || !data.Any()) return;

            bool externalTransaction = transaction != null;
            var conn = (SqlConnection)(externalTransaction ? transaction.Connection : context.Database.Connection);
                if (conn.State != ConnectionState.Open) conn.Open();
            if (!externalTransaction)
                transaction = conn.BeginTransaction();

            var mapping = GetEntityMapping(context, typeof(T), out ColumnMapping keyCol);
            if (keyCol == null)
                throw new InvalidOperationException($"Entity {typeof(T).Name} Has No [Key] Property.");

            string tableName = $"[{mapping.Schema}].[{mapping.TableName}]";
            string tempColumn = "TempGuid";

            // 1. اضافه کردن ستون TempGuid به جدول اصلی
            string addColSql = $"ALTER TABLE {tableName} ADD [{tempColumn}] UNIQUEIDENTIFIER NULL;";
            using (var addCmd = new SqlCommand(addColSql, conn, transaction))
                addCmd.ExecuteNonQuery();

            try
            {
                // 2. آماده‌سازی جدول داده‌ها
                var table = new DataTable();
                table.Columns.Add(tempColumn, typeof(Guid));
                foreach (var col in mapping.Columns)
                    table.Columns.Add(col.DbColumnName, Nullable.GetUnderlyingType(col.PropertyType) ?? col.PropertyType);

                var guidList = new List<Guid>();
                foreach (var item in data)
                {
                    var guid = Guid.NewGuid();
                    guidList.Add(guid);
                    var values = new object[] { guid }
                        .Concat(mapping.Columns.Select(c => c.Property.GetValue(item) ?? DBNull.Value))
                        .ToArray();
                    table.Rows.Add(values);
                }

                // 3. درج داده‌ها در جدول موقت SQL
                string tempTableName = $"#TempInsert_{mapping.TableName}_{Guid.NewGuid():N}";
                string createTempSql = $@"
                CREATE TABLE {tempTableName} (
                    [{tempColumn}] UNIQUEIDENTIFIER NOT NULL,
                    {string.Join(", ", mapping.Columns.Select(c => $"[{c.DbColumnName}] {c.SqlType}{c.VarCharLengthStr}{c.CollateStr} NULL"))}
                )";
                using (var createCmd = new SqlCommand(createTempSql, conn, transaction))
                    createCmd.ExecuteNonQuery();

                using (var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, transaction))
                {
                    bulkCopy.DestinationTableName = tempTableName;
                    bulkCopy.BatchSize = 5000;
                    bulkCopy.BulkCopyTimeout = 0;

                    bulkCopy.ColumnMappings.Add(tempColumn, tempColumn);
                    foreach (var col in mapping.Columns)
                        bulkCopy.ColumnMappings.Add(col.DbColumnName, col.DbColumnName);

                    bulkCopy.WriteToServer(table);
                }

                // 4. انتقال داده‌ها از جدول موقت به جدول اصلی با استفاده از ستون TempGuid
                var insertColumns = string.Join(", ", mapping.Columns.Select(c => $"[{c.DbColumnName}]")) + $", [{tempColumn}]";
                var selectColumns = string.Join(", ", mapping.Columns.Select(c => $"src.[{c.DbColumnName}]")) + $", src.[{tempColumn}]";

                string insertSql = $@"
                INSERT INTO {tableName} ({insertColumns})
                OUTPUT inserted.[{keyCol.DbColumnName}], inserted.[{tempColumn}]
                SELECT {selectColumns}
                FROM {tempTableName} AS src;";

                var idMap = new Dictionary<Guid, object>();
                using (var cmd = new SqlCommand(insertSql, conn, transaction))
                {
                    cmd.CommandTimeout = 0;
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var insertedId = reader.GetValue(0);
                            var tempGuid = reader.GetGuid(1);
                            idMap[tempGuid] = insertedId;
                        }
                    }
                }

                // 5. مقداردهی Id به آبجکت‌ها
                for (int i = 0; i < data.Count; i++)
                {
                    var guid = guidList[i];
                    if (idMap.TryGetValue(guid, out var id))
                        keyCol.Property.SetValue(data[i], Convert.ChangeType(id, keyCol.PropertyType));
                }

                // 6. حذف جدول موقت
                using (var dropTemp = new SqlCommand($"DROP TABLE {tempTableName};", conn, transaction))
                    dropTemp.ExecuteNonQuery();
            }
            catch(Exception ex)
            {
                if (!externalTransaction)
                {
                    transaction.Rollback();
                }
                throw ex;
            }
            finally
            {
                // 7. حذف ستون موقت از جدول اصلی
                string dropColSql = $"ALTER TABLE {tableName} DROP COLUMN [{tempColumn}];";
                using (var dropCmd = new SqlCommand(dropColSql, conn, transaction))
                    dropCmd.ExecuteNonQuery();

                if (!externalTransaction)
                {
                    transaction.Commit();
                    conn.Close();
                    conn.Dispose();
                }
            }
        }

        #endregion

        #region Bulk Update
        public static void BulkUpdate<T>(this DbContext context, IEnumerable<T> data)
        {

            var conn = (SqlConnection)context.Database.Connection;
            {
                if (conn.State != ConnectionState.Open) conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    context.BulkUpdate(data, transaction);
                    transaction.Commit();
                }
            }
        }

        public static void BulkUpdate<T>(this DbContext context, IEnumerable<T> data, SqlTransaction transaction)
        {
            if (data == null || !data.Any()) return;

            var mapping = GetEntityMapping(context, typeof(T), out ColumnMapping keyCol);
            if (keyCol == null)
                throw new InvalidOperationException($"No Primary Key Defined For Entity {typeof(T).Name}");
            bool removeIdentity = false;
            // کلید اگر identity باشد در فهرست نگاشت ظاهر نمی شود، بنابراین باید دستی انرا اضافه کرد
            if (!mapping.Columns.Any(c => c.DbColumnName == keyCol.DbColumnName))
            {
                mapping.Columns.Add(keyCol);
                removeIdentity = true;
            }
            string tableName = $"[{mapping.Schema}].[{mapping.TableName}]";
            string tempTableName = $"#Temp_{mapping.TableName}_{Guid.NewGuid():N}";

            var table = new DataTable();
            foreach (var col in mapping.Columns)
                table.Columns.Add(col.DbColumnName, Nullable.GetUnderlyingType(col.PropertyType) ?? col.PropertyType);

            foreach (var item in data)
            {
                var values = mapping.Columns.Select(c => c.Property.GetValue(item) ?? DBNull.Value).ToArray();
                table.Rows.Add(values);
            }

            // Create temp table with precise SQL types
            var createTempSql = $"CREATE TABLE {tempTableName} ({string.Join(", ", mapping.Columns.Select(c => $"[{c.DbColumnName}] {c.SqlType}{c.VarCharLengthStr}{c.CollateStr} NULL"))})";
            using (var cmd = new SqlCommand(createTempSql, transaction.Connection, transaction))
                cmd.ExecuteNonQuery();

            using (var bulkCopy = new SqlBulkCopy(transaction.Connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = tempTableName;
                bulkCopy.BatchSize = 5000;
                bulkCopy.BulkCopyTimeout = 0;
                foreach (var col in mapping.Columns)
                    bulkCopy.ColumnMappings.Add(col.DbColumnName, col.DbColumnName);
                bulkCopy.WriteToServer(table);
            };


            // Update only changed columns
            var setClauses = mapping.Columns
                .Where(c => c.DbColumnName != keyCol.DbColumnName)
                .Select(c =>
                {
                    string sqlCompare = GetSqlComparison(c.PropertyType, c.DbColumnName);
                    return $"Target.[{c.DbColumnName}] = CASE WHEN {sqlCompare} THEN Source.[{c.DbColumnName}] ELSE Target.[{c.DbColumnName}] END";
                });

            string updateSql = $@"
                                  UPDATE Target
                                  SET {string.Join(", ", setClauses)}
                                  FROM {tableName} AS Target
                                  INNER JOIN {tempTableName} AS Source
                                  ON Target.[{keyCol.DbColumnName}] = Source.[{keyCol.DbColumnName}]";

            using (var cmd = new SqlCommand(updateSql, transaction.Connection, transaction))
            {
                cmd.CommandTimeout = 0;
                cmd.ExecuteNonQuery();
            }
            // از انجایی که مثل کارتهای گارانتی ممکن است بروزرسانی و ایجاد پشت سر هم باشند
            if (removeIdentity)
            {
                mapping.Columns.Remove(keyCol);
            }
        }
        #endregion

        #region Bulk Delete
        public static void BulkDelete<T>(this DbContext context, IEnumerable<T> data)
        {
            using (var conn = (SqlConnection)context.Database.Connection)
            {
                if (conn.State != ConnectionState.Open) conn.Open();
                using (var transaction = conn.BeginTransaction())
                {
                    context.BulkDelete(data, transaction);
                    transaction.Commit();
                }
            }
        }

        public static void BulkDelete<T>(this DbContext context, IEnumerable<T> data, SqlTransaction transaction)
        {
            if (data == null || !data.Any()) return;

            var mapping = GetEntityMapping(context, typeof(T), out ColumnMapping keyColumn);
            if (keyColumn == null)
                throw new InvalidOperationException($"No Primary Key Defined For Entity {typeof(T).Name}");

            string tableName = $"[{mapping.Schema}].[{mapping.TableName}]";
            var keyValues = data.Select(d => keyColumn.Property.GetValue(d)).ToList();
            if (!keyValues.Any()) return;

            string tempTableName = $"#TempDelete_{mapping.TableName}_{Guid.NewGuid():N}";
            var table = new DataTable();
            table.Columns.Add(keyColumn.DbColumnName, Nullable.GetUnderlyingType(keyColumn.PropertyType) ?? keyColumn.PropertyType);
            foreach (var val in keyValues)
                table.Rows.Add(val ?? DBNull.Value);

            string createTempSql = $"CREATE TABLE {tempTableName} ([{keyColumn.DbColumnName}] {keyColumn.SqlType} NULL)";
            using (var cmd = new SqlCommand(createTempSql, transaction.Connection, transaction))
                cmd.ExecuteNonQuery();

            using (var bulkCopy = new SqlBulkCopy(transaction.Connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = tempTableName;
                bulkCopy.BatchSize = 5000;
                bulkCopy.BulkCopyTimeout = 0;
                bulkCopy.ColumnMappings.Add(keyColumn.DbColumnName, keyColumn.DbColumnName);
                bulkCopy.WriteToServer(table);
            }



            string deleteSql = $@"
                                  DELETE Target
                                  FROM {tableName} AS Target
                                  INNER JOIN {tempTableName} AS Source
                                  ON Target.[{keyColumn.DbColumnName}] = Source.[{keyColumn.DbColumnName}]";

            using (var cmd = new SqlCommand(deleteSql, transaction.Connection, transaction))
            {
                cmd.CommandTimeout = 0;
                cmd.ExecuteNonQuery();
            }
        }
        #endregion

        #region Helpers
        private static string GetSqlComparison(Type type, string columnName)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) 
                return $"ISNULL(Target.[{columnName}], '') <> ISNULL(Source.[{columnName}], '')";
            if (type == typeof(int) || type == typeof(long) || type == typeof(decimal) || type == typeof(double) || type == typeof(byte)) 
                return $"ISNULL(Target.[{columnName}], 0) <> ISNULL(Source.[{columnName}], 0)";
            if (type == typeof(bool)) 
                return $"ISNULL(Target.[{columnName}], 0) <> ISNULL(Source.[{columnName}], 0)";
            if (type == typeof(DateTime)) 
                return $"ISNULL(Target.[{columnName}], '1900-01-01') <> ISNULL(Source.[{columnName}], '1900-01-01')";
            if (type == typeof(Guid)) 
                return $"ISNULL(Target.[{columnName}], '00000000-0000-0000-0000-000000000000') <> ISNULL(Source.[{columnName}], '00000000-0000-0000-0000-000000000000')";
            throw new NotSupportedException($"Type {type.Name} not supported for comparison");
        }

        private static EntityMapping GetEntityMapping(DbContext context, Type entityType, out ColumnMapping keyCol)
        {
            keyCol = null;
            if (_entityMappingCache.TryGetValue(entityType, out var cached))
            {
                _keyCache.TryGetValue(entityType, out keyCol);
                return cached;
            }

            entityType = System.Data.Entity.Core.Objects.ObjectContext.GetObjectType(entityType);
            var objectContext = ((IObjectContextAdapter)context).ObjectContext;
            var metadata = objectContext.MetadataWorkspace;
            var objectItemCollection = (ObjectItemCollection)metadata.GetItemCollection(DataSpace.OSpace);

            // پیدا کردن OSpace Entity
            EntityType ospaceEntity = null;
            foreach (var e in metadata.GetItems<EntityType>(DataSpace.OSpace))
            {
                try
                {
                    if (objectItemCollection.GetClrType(e) == entityType)
                    {
                        ospaceEntity = e;
                        break;
                    }
                }
                catch { continue; }
            }
            if (ospaceEntity == null) throw new InvalidOperationException($"Cannot Find Mapping For Entity {entityType.Name}");

            // گرفتن EntitySetMapping
            EntitySetMapping entitySetMapping = null;
            foreach (var containerMapping in metadata.GetItems<EntityContainerMapping>(DataSpace.CSSpace))
            {
                foreach (var esm in containerMapping.EntitySetMappings)
                {
                    foreach (var etm in esm.EntityTypeMappings)
                    {
                        try
                        {
                            if (etm.Fragments.Any(f => f.StoreEntitySet.ElementType.Name == ospaceEntity.Name))
                            {
                                entitySetMapping = esm;
                                break;
                            }
                        }
                        catch { continue; }
                    }
                    if (entitySetMapping != null) break;
                }
                if (entitySetMapping != null) break;
            }
            string keyName = ospaceEntity.KeyProperties.FirstOrDefault()?.Name;
            string schema = "dbo";
            string tableName = entityType.Name;
            var columnMappings = new List<ColumnMapping>();

            if (entitySetMapping != null)
            {
                var mappingFragment = entitySetMapping.EntityTypeMappings.SelectMany(etm => etm.Fragments).FirstOrDefault();
                if (mappingFragment != null)
                {
                    tableName = mappingFragment.StoreEntitySet.Table ?? mappingFragment.StoreEntitySet.Name;
                    schema = mappingFragment.StoreEntitySet.Schema ?? "dbo";

                    foreach (var propMap in mappingFragment.PropertyMappings.OfType<ScalarPropertyMapping>())
                    {
                        var clrProp = entityType.GetProperty(propMap.Property.Name);
                        if (clrProp == null) continue;

                        string dbColumnName = propMap.Column?.Name ?? clrProp.GetCustomAttribute<ColumnAttribute>()?.Name ?? clrProp.Name;
                        if (clrProp.Name == keyName)
                        {
                            keyCol = new ColumnMapping
                            {
                                Property = clrProp,
                                DbColumnName = dbColumnName,
                                PropertyType = clrProp.PropertyType,
                                SqlType = GetSqlType(propMap, clrProp.PropertyType)
                            };
                            _keyCache[entityType] = keyCol;
                        }
                        if (Attribute.IsDefined(clrProp, typeof(NotMappedAttribute)) ||
                            Attribute.IsDefined(clrProp, typeof(DatabaseGeneratedAttribute))) continue;
                        if (propMap.Column?.StoreGeneratedPattern == StoreGeneratedPattern.Identity || propMap.Column?.StoreGeneratedPattern == StoreGeneratedPattern.Computed)
                            continue;
                        int? maxLength = null;

                        if (propMap.Column != null)
                        {
                            var facet = propMap.Column.TypeUsage.Facets.FirstOrDefault(f => f.Name == "MaxLength");
                            if (facet?.Value != null)
                                maxLength = (int?)facet.Value;
                        }

                        if (maxLength == null)
                        {
                            var maxLenAttr = clrProp.GetCustomAttribute<MaxLengthAttribute>();
                            var stringLenAttr = clrProp.GetCustomAttribute<StringLengthAttribute>();
                            if (maxLenAttr != null)
                                maxLength = maxLenAttr.Length;
                            else if (stringLenAttr != null)
                                maxLength = stringLenAttr.MaximumLength;
                        }

                        columnMappings.Add(new ColumnMapping
                        {
                            Property = clrProp,
                            DbColumnName = dbColumnName,
                            PropertyType = clrProp.PropertyType,
                            SqlType = GetSqlType(propMap, clrProp.PropertyType),
                            MaxLength = propMap.Column.MaxLength
                        });
                    }
                }
            }
            else
            {
                // fallback DataAnnotation-only
                columnMappings = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => !Attribute.IsDefined(p, typeof(NotMappedAttribute)))
                    .Select(p =>
                    {
                        var colAttr = p.GetCustomAttribute<ColumnAttribute>();
                        string dbName = colAttr?.Name ?? p.Name;
                        return new ColumnMapping
                        {
                            Property = p,
                            DbColumnName = dbName,
                            PropertyType = p.PropertyType,
                            SqlType = GetSqlType(null, p.PropertyType)
                        };
                    }).ToList();
            }

            var result = new EntityMapping { Schema = schema, TableName = tableName, Columns = columnMappings };
            _entityMappingCache[entityType] = result;
            return result;
        }

        private class EntityMapping
        {
            public string Schema { get; set; }
            public string TableName { get; set; }
            public List<ColumnMapping> Columns { get; set; }
        }

        private class ColumnMapping
        {
            public PropertyInfo Property { get; set; }
            public string DbColumnName { get; set; }
            public Type PropertyType { get; set; }
            public string SqlType { get; set; } // دقیقاً همون TypeName دیتابیس
            public int? MaxLength { get; set; }
            public string VarCharLengthStr => SqlType == "nvarchar" || SqlType == "varchar" ? MaxLength > 0 ? $"({MaxLength})" : "(max)" : "";
            public string CollateStr => SqlType.Contains("char") ? " COLLATE Arabic_CI_AS" : "";
        }

        private static string GetSqlType(ScalarPropertyMapping propMap, Type propertyType)
        {
            propertyType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

            if (propMap?.Column != null && !string.IsNullOrEmpty(propMap.Column.TypeName))
                return propMap.Column.TypeName;

            // fallback ساده
            if (propertyType == typeof(int)) return "INT";
            if (propertyType == typeof(long)) return "BIGINT";
            if (propertyType == typeof(decimal)) return "DECIMAL(18,2)";
            if (propertyType == typeof(double)) return "FLOAT";
            if (propertyType == typeof(bool)) return "BIT";
            if (propertyType == typeof(DateTime)) return "DATETIME";
            if (propertyType == typeof(Guid)) return "UNIQUEIDENTIFIER";
            if (propertyType == typeof(string)) return "NVARCHAR(MAX)";

            throw new NotSupportedException($"Type {propertyType.Name} not supported");
        }
        #endregion
    }
}
