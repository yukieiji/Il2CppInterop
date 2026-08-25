using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Il2CppInterop.Runtime.Runtime;

namespace Il2CppInterop.Runtime.InteropTypes;

using System;
using System.Reflection;
using System.Reflection.Emit;

public static class DynamicAdapter
{
    public static TTarget? CreateAdapter<TTarget>(object source, IntPtr ptrValue = default) where TTarget : class
    {
        Type targetType = typeof(TTarget);
        Type sourceType = source.GetType();

        // 1. 動的アセンブリとモジュールの作成
        AssemblyName assemblyName = new AssemblyName("DynamicAdapterAssembly");
        AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");

        // 2. TTarget(クラス A) を継承した動的クラスを定義
        string typeName = $"{targetType.Name}_ProxyFor_{sourceType.Name}_{Guid.NewGuid():N}";
        TypeBuilder typeBuilder = moduleBuilder.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, targetType);

        // 3. 内部で保持する source (クラス B のインスタンス) のフィールドを定義
        FieldBuilder sourceField = typeBuilder.DefineField("_source", typeof(object), FieldAttributes.Private);

        // ---------------------------------------------------------
        // 4. コンストラクタの生成 (IntPtr を渡して base(IntPtr) を呼び出す)
        // ---------------------------------------------------------
        ConstructorBuilder ctor = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
        ILGenerator ctorIl = ctor.GetILGenerator();

        // クラス A の A(IntPtr) コンストラクタを検索
        ConstructorInfo baseCtor = targetType.GetConstructor(new[] { typeof(IntPtr) })
            ?? throw new InvalidOperationException($"{targetType.Name} に (IntPtr) を受取るコンストラクタが見つかりません。");

        // base(ptr) の呼び出し
        ctorIl.Emit(OpCodes.Ldarg_0); // this
        ctorIl.Emit(OpCodes.Ldarg_2); // 引数で渡された IntPtr (第2引数)
        ctorIl.Emit(OpCodes.Call, baseCtor);

        // this._source = source
        ctorIl.Emit(OpCodes.Ldarg_0);
        ctorIl.Emit(OpCodes.Ldarg_1); // 第1引数の object source
        ctorIl.Emit(OpCodes.Stfld, sourceField);
        ctorIl.Emit(OpCodes.Ret);

        // 5. TTarget (クラス A) の virtual メソッドをオーバーライド
        MethodInfo[] targetMethods = targetType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (var targetMethod in targetMethods)
        {
            if (targetMethod.DeclaringType == typeof(object) || !targetMethod.IsVirtual || targetMethod.IsFinal)
            {
                continue;
            }

            ParameterInfo[] parameters = targetMethod.GetParameters();
            Type[] paramTypes = Array.ConvertAll(parameters, p => p.ParameterType);
            MethodInfo? sourceMethod = sourceType.GetMethod(targetMethod.Name, paramTypes);

            if (sourceMethod == null)
            {
                continue;
            }

            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                targetMethod.Name,
                MethodAttributes.Public | MethodAttributes.Virtual,
                targetMethod.ReturnType,
                paramTypes
            );

            ILGenerator il = methodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, sourceField);
            il.Emit(OpCodes.Castclass, sourceType);

            for (int i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Ldarg, i + 1);
            }

            il.Emit(OpCodes.Callvirt, sourceMethod);
            il.Emit(OpCodes.Ret);

            typeBuilder.DefineMethodOverride(methodBuilder, targetMethod);
        }

        // 6. インスタンス化 (引数として source と ptrValue を渡す)
        Type? proxyType = typeBuilder.CreateType();
        if (proxyType == null)
        {
            return null;
        }
        return (TTarget?)Activator.CreateInstance(proxyType, source, ptrValue);
    }
}

public class Il2CppObjectBase
{
    private static readonly MethodInfo _unboxMethod = typeof(Il2CppObjectBase).GetMethod(nameof(Unbox));
    internal bool isWrapped;
    internal IntPtr pooledPtr;

    private nint myGcHandle;

    public Il2CppObjectBase(IntPtr pointer)
    {
        myGcHandle = pointer;
    }

    public IntPtr ObjectClass => IL2CPP.il2cpp_object_get_class(Pointer);

    public IntPtr Pointer
    {
        get
        {
            var handleTarget = IL2CPP.il2cpp_gchandle_get_target(myGcHandle);
            if (handleTarget == IntPtr.Zero)
                throw new ObjectCollectedException("Object was garbage collected in IL2CPP domain");
            return handleTarget;
        }
    }

    public bool WasCollected
    {
        get
        {
            return false;
        }
    }

    internal void CreateGCHandle(IntPtr objHdl)
    {
        if (objHdl == IntPtr.Zero)
            throw new NullReferenceException();

        // This object already wraps an Il2Cpp object, skip the pointer and let it be GC'd
        if (isWrapped)
            return;

        myGcHandle = IL2CPP.il2cpp_gchandle_new(objHdl, false);
    }

    public T Cast<T>() where T : Il2CppObjectBase
    {
        return TryCast<T>() ?? throw new InvalidCastException(
            $"Can't cast object of type {this.GetType()} to type {typeof(T)}");
    }

    internal static unsafe T UnboxUnsafe<T>(IntPtr pointer)
    {
        var nestedTypeClassPointer = Il2CppClassPointerStore<T>.NativeClassPtr;
        if (nestedTypeClassPointer == IntPtr.Zero)
            throw new ArgumentException($"{typeof(T)} is not an Il2Cpp reference type");

        var ownClass = IL2CPP.il2cpp_object_get_class(pointer);
        if (!IL2CPP.il2cpp_class_is_assignable_from(nestedTypeClassPointer, ownClass))
            throw new InvalidCastException(
                $"Can't cast object of type {IL2CPP.il2cpp_class_get_name_(ownClass)} to type {typeof(T)}");

        return Unsafe.AsRef<T>(IL2CPP.il2cpp_object_unbox(pointer).ToPointer());
    }

    public T Unbox<T>() where T : unmanaged
    {
        return UnboxUnsafe<T>(Pointer);
    }

    private static readonly Type[] _intPtrTypeArray = { typeof(IntPtr) };
    private static readonly MethodInfo _getUninitializedObject = typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetUninitializedObject))!;
    private static readonly MethodInfo _getTypeFromHandle = typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!;
    private static readonly MethodInfo _createGCHandle = typeof(Il2CppObjectBase).GetMethod(nameof(CreateGCHandle), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo _isWrapped = typeof(Il2CppObjectBase).GetField(nameof(isWrapped), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static class InitializerStore<T>
    {
        private static Func<IntPtr, T>? _initializer;

        private static Func<IntPtr, T> Create()
        {
            var type = Il2CppClassPointerStore<T>.CreatedTypeRedirect ?? typeof(T);

            var dynamicMethod = new DynamicMethod($"Initializer<{typeof(T).AssemblyQualifiedName}>", type, _intPtrTypeArray);
            dynamicMethod.DefineParameter(0, ParameterAttributes.None, "pointer");

            var il = dynamicMethod.GetILGenerator();

            if (type.GetConstructor(new[] { typeof(IntPtr) }) is { } pointerConstructor)
            {
                // Base case: Il2Cpp constructor => call it directly
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, pointerConstructor);
            }
            else
            {
                // Special case: We have a parameterless constructor
                // However, it could be be user-made or implicit
                // In that case we set the GCHandle and then call the ctor and let GC destroy any objects created by DerivedConstructorPointer

                // var obj = (T)RuntimeHelpers.GetUninitializedObject(type);
                il.Emit(OpCodes.Ldtoken, type);
                il.Emit(OpCodes.Call, _getTypeFromHandle);
                il.Emit(OpCodes.Call, _getUninitializedObject);
                il.Emit(OpCodes.Castclass, type);

                // obj.CreateGCHandle(pointer);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Callvirt, _createGCHandle);

                // obj.isWrapped = true;
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stfld, _isWrapped);

                var parameterlessConstructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
                if (parameterlessConstructor != null)
                {
                    // obj..ctor();
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Callvirt, parameterlessConstructor);
                }
            }

            il.Emit(OpCodes.Ret);

            return dynamicMethod.CreateDelegate<Func<IntPtr, T>>();
        }

        public static Func<IntPtr, T> Initializer => _initializer ??= Create();
    }

    public T? TryCast<T>() where T : Il2CppObjectBase
    {
        return DynamicAdapter.CreateAdapter<T>(this);
    }
}
