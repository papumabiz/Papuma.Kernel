// Copyright (c) 2026- by Harald Lapp.
// Licensed under the MIT License. See LICENSE in the repository root for details.

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Model;

/// <summary>
/// Builds the kernel metamodel at startup (architecture §6). Policy and key defaults
/// come from attributes on the document classes; fluent configuration overrides them
/// per deployment (ADR-007).
/// </summary>
public sealed class KernelModelBuilder
{
    private readonly Dictionary<Type, IDocumentTypeBuilder> _builders = [];

    /// <summary>
    /// Registers a document type using attribute defaults only.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    public KernelModelBuilder Document<T>() where T : class => Document<T>(_ => { });

    /// <summary>
    /// Registers a document type with fluent overrides on top of the attribute defaults.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    public KernelModelBuilder Document<T>(Action<DocumentTypeBuilder<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (_builders.ContainsKey(typeof(T)))
        {
            throw new InvalidOperationException($"Document type {typeof(T).Name} is already registered.");
        }

        var builder = new DocumentTypeBuilder<T>();
        configure(builder);
        _builders[typeof(T)] = builder;
        return this;
    }

    /// <summary>
    /// Builds the immutable model. Throws when a registered type has no resolvable id.
    /// </summary>
    public KernelModel Build()
    {
        var byType = new Dictionary<Type, DocumentTypeMetadata>();
        foreach (var (clrType, builder) in _builders)
        {
            byType[clrType] = builder.Build();
        }

        return new KernelModel(byType);
    }

    private interface IDocumentTypeBuilder
    {
        DocumentTypeMetadata Build();
    }

    /// <summary>
    /// Fluent configuration of one document type.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    public sealed class DocumentTypeBuilder<T> : IDocumentTypeBuilder where T : class
    {
        private readonly Dictionary<string, FieldPolicy> _policyOverrides = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _keyOverrides = new(StringComparer.Ordinal); // path → unique
        private readonly Dictionary<int, Action<System.Text.Json.Nodes.JsonObject>> _upcasters = [];
        private LambdaExpression? _idExpression;

        internal DocumentTypeBuilder()
        {
        }

        /// <summary>
        /// Declares the id property explicitly. Without this call, a string property
        /// named <c>Id</c> is required by convention.
        /// </summary>
        public DocumentTypeBuilder<T> HasId(Expression<Func<T, string>> id)
        {
            ArgumentNullException.ThrowIfNull(id);
            _idExpression = id;
            return this;
        }

        /// <summary>
        /// Starts a policy override for a property (overrides any attribute default).
        /// </summary>
        public PropertyPolicyBuilder Property(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            return new PropertyPolicyBuilder(this, JsonPathResolver.Resolve(property));
        }

        /// <summary>
        /// Declares a unique key on a property (ADR-006).
        /// </summary>
        public DocumentTypeBuilder<T> UniqueKey(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            _keyOverrides[JsonPathResolver.Resolve(property)] = true;
            return this;
        }

        /// <summary>
        /// Declares a non-unique lookup key on a property (ADR-006).
        /// </summary>
        public DocumentTypeBuilder<T> LookupKey(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            _keyOverrides[JsonPathResolver.Resolve(property)] = false;
            return this;
        }

        /// <summary>
        /// Registers an upcaster that lifts raw documents from
        /// <paramref name="fromVersion"/> to <paramref name="fromVersion"/> + 1 (ADR-005).
        /// The current schema version of the type becomes the highest <c>fromVersion</c> + 1.
        /// </summary>
        /// <remarks>
        /// Only transforming changes need an upcaster — renames, restructurings, type
        /// changes, derived defaults. Additive changes (new optional property with a
        /// constant default, removed property, new enum value) are covered by JSON
        /// deserialization and require neither an upcaster nor a version bump.
        /// Never simulate a transformation additively (ADR-005, point 7). Upcasters must
        /// not be removed while documents of their source version may still exist.
        /// </remarks>
        /// <param name="fromVersion">The schema version this upcaster reads (≥ 1).</param>
        /// <param name="upcast">Mutates the raw JSON document in place to the next version.</param>
        public DocumentTypeBuilder<T> Upcast(int fromVersion, Action<System.Text.Json.Nodes.JsonObject> upcast)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 1);
            ArgumentNullException.ThrowIfNull(upcast);

            if (!_upcasters.TryAdd(fromVersion, upcast))
            {
                throw new InvalidOperationException(
                    $"An upcaster from version {fromVersion} is already registered for {typeof(T).Name}.");
            }

            return this;
        }

        DocumentTypeMetadata IDocumentTypeBuilder.Build()
        {
            var clrType = typeof(T);
            var name = clrType.Name;
            InputValidator.ValidateDocumentType(name);

            // 1. Attribute defaults, scanned recursively into nested POCO types.
            var policies = new Dictionary<string, FieldPolicy>(StringComparer.Ordinal);
            var keys = new Dictionary<string, bool>(StringComparer.Ordinal);
            ScanType(clrType, prefix: string.Empty, policies, keys, visited: []);

            // 2. Fluent overrides win.
            foreach (var (path, policy) in _policyOverrides)
            {
                if (policy == FieldPolicy.Track)
                {
                    policies.Remove(path); // explicit Track() resets an attribute default
                }
                else
                {
                    policies[path] = policy;
                }
            }

            foreach (var (path, unique) in _keyOverrides)
            {
                keys[path] = unique;
            }

            var keyMetadata = keys
                .OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(k => new KeyMetadata(k.Key, k.Value, BuildIndexName(name, k.Key, k.Value)))
                .ToList();

            // The upcaster chain must be contiguous from 1: lazy upcasting may encounter
            // documents of any historical version (ADR-005).
            var orderedUpcasters = new List<Action<System.Text.Json.Nodes.JsonObject>>(_upcasters.Count);
            for (var version = 1; version <= _upcasters.Count; version++)
            {
                if (!_upcasters.TryGetValue(version, out var upcaster))
                {
                    throw new InvalidOperationException(
                        $"Document type {name}: upcaster chain has a gap — no upcaster from version {version}, " +
                        $"but versions up to {_upcasters.Keys.Max()} are registered. " +
                        "Upcasters must form a contiguous chain starting at 1.");
                }

                orderedUpcasters.Add(upcaster);
            }

            return new DocumentTypeMetadata(name, clrType, policies, keyMetadata, BuildIdGetter(clrType), orderedUpcasters);
        }

        internal void SetPolicy(string path, FieldPolicy policy) => _policyOverrides[path] = policy;

        private Func<object, string?> BuildIdGetter(Type clrType)
        {
            PropertyInfo idProperty;
            if (_idExpression is not null)
            {
                var body = _idExpression.Body is UnaryExpression { NodeType: ExpressionType.Convert } u
                    ? u.Operand
                    : _idExpression.Body;
                idProperty = (PropertyInfo)((MemberExpression)body).Member;
            }
            else
            {
                idProperty = clrType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException(
                        $"Document type {clrType.Name} has no public 'Id' property. " +
                        "Add one or declare the id via HasId(...).");

                if (idProperty.PropertyType != typeof(string))
                {
                    throw new InvalidOperationException(
                        $"Document type {clrType.Name}: the conventional 'Id' property must be a string.");
                }
            }

            return document => (string?)idProperty.GetValue(document);
        }

        /// <summary>
        /// Fluent policy selection for one property.
        /// </summary>
        public sealed class PropertyPolicyBuilder
        {
            private readonly DocumentTypeBuilder<T> _parent;
            private readonly string _path;

            internal PropertyPolicyBuilder(DocumentTypeBuilder<T> parent, string path)
            {
                _parent = parent;
                _path = path;
            }

            /// <summary>Records old and new value verbatim (resets an attribute default).</summary>
            public DocumentTypeBuilder<T> Track() => Set(FieldPolicy.Track);

            /// <summary>Records only that the field changed.</summary>
            public DocumentTypeBuilder<T> Redact() => Set(FieldPolicy.Redact);

            /// <summary>Records a reference to the value's location instead of the value.</summary>
            public DocumentTypeBuilder<T> StoreAsReference() => Set(FieldPolicy.Reference);

            /// <summary>Records a change marker plus a SHA-256 hash of the new value.</summary>
            public DocumentTypeBuilder<T> StoreAsHash() => Set(FieldPolicy.Hash);

            /// <summary>Excludes the field from diffs entirely.</summary>
            public DocumentTypeBuilder<T> DoNotTrack() => Set(FieldPolicy.DoNotTrack);

            private DocumentTypeBuilder<T> Set(FieldPolicy policy)
            {
                _parent.SetPolicy(_path, policy);
                return _parent;
            }
        }
    }

    private static void ScanType(
        Type type,
        string prefix,
        Dictionary<string, FieldPolicy> policies,
        Dictionary<string, bool> keys,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return; // cycle guard
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var jsonName = JsonPathResolver.JsonNameOf(property);
            var path = prefix.Length == 0 ? jsonName : $"{prefix}.{jsonName}";

            var policy = PolicyFromAttributes(property);
            if (policy is not null)
            {
                policies[path] = policy.Value;
            }

            if (property.GetCustomAttribute<UniqueKeyAttribute>() is not null)
            {
                keys[path] = true;
            }
            else if (property.GetCustomAttribute<LookupKeyAttribute>() is not null)
            {
                keys[path] = false;
            }

            if (IsNestedPocoType(property.PropertyType))
            {
                ScanType(property.PropertyType, path, policies, keys, visited);
            }
        }

        visited.Remove(type);
    }

    private static FieldPolicy? PolicyFromAttributes(PropertyInfo property)
    {
        if (property.GetCustomAttribute<DoNotTrackAttribute>() is not null)
        {
            return FieldPolicy.DoNotTrack;
        }

        if (property.GetCustomAttribute<TrackHashAttribute>() is not null)
        {
            return FieldPolicy.Hash;
        }

        if (property.GetCustomAttribute<TrackReferenceAttribute>() is not null)
        {
            return FieldPolicy.Reference;
        }

        if (property.GetCustomAttribute<SensitiveDataAttribute>() is not null)
        {
            return FieldPolicy.Redact;
        }

        return null;
    }

    private static bool IsNestedPocoType(Type type)
    {
        if (!type.IsClass || type == typeof(string))
        {
            return false;
        }

        // Collections and dictionaries are atomic diff values (ADR-004) — policies on
        // their element types cannot be applied per element and are not scanned.
        return !typeof(IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>
    /// Builds a deterministic PostgreSQL index name (≤ 63 chars) for a declared key.
    /// </summary>
    internal static string BuildIndexName(string documentType, string path, bool unique)
    {
        var prefix = unique ? "ux" : "ix";
        var raw = $"{prefix}_papuma_doc_{documentType}_{path.Replace('.', '_')}".ToLowerInvariant();
        if (raw.Length <= 63)
        {
            return raw;
        }

        // PostgreSQL truncates identifiers at 63 bytes — disambiguate with a stable hash.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8];
        return $"{raw[..54]}_{hash}";
    }
}
