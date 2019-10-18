using System;
using System.Threading.Tasks;
using System.IO;
using Android.Graphics;
using System.Threading;
using Android.Content;
using Android.App;
using Android.Graphics.Drawables;
using System.Collections.Generic;
using System.Linq;

using Path = System.IO.Path;
using System.Reflection;

namespace Splat
{
    public class PlatformBitmapLoader : IBitmapLoader
    {
        readonly Dictionary<string, int> drawableList;

        public PlatformBitmapLoader()
        {
            drawableList = GetDrawableList();
        }

        public async Task<IBitmap> Load(Stream sourceStream, float? desiredWidth, float? desiredHeight)
        {
            Bitmap bitmap = null;

            if (desiredWidth == null)
            {
                bitmap = await Task.Run(() => BitmapFactory.DecodeStream(sourceStream));
            }
            else
            {
                var opts = new BitmapFactory.Options()
                {
                    OutWidth = (int)desiredWidth.Value,
                    OutHeight = (int)desiredHeight.Value,
                };

                var noPadding = new Rect(0, 0, 0, 0);
                bitmap = await Task.Run(() => BitmapFactory.DecodeStream(sourceStream, noPadding, opts));
            }

            if (bitmap == null)
            {
                throw new IOException("Failed to load bitmap from source stream");
            }

            return bitmap.FromNative();
        }

        public Task<IBitmap> LoadFromResource(string source, float? desiredWidth, float? desiredHeight)
        {
            var res = Application.Context.Resources;

            var id = default(int);
            if (Int32.TryParse(source, out id))
            {
                return Task.Run(() => (IBitmap)new DrawableBitmap(res.GetDrawable(id)));
            }

            if (drawableList.ContainsKey(source))
            {
                return Task.Run(() => (IBitmap)new DrawableBitmap(res.GetDrawable(drawableList[source])));
            }

            // NB: On iOS, you have to pass the extension, but on Android it's 
            // stripped - try stripping the extension to see if there's a Drawable.
            var key = Path.GetFileNameWithoutExtension(source);
            if (drawableList.ContainsKey(key))
            {
                return Task.Run(() => (IBitmap)new DrawableBitmap(res.GetDrawable(drawableList[key])));
            }

            throw new ArgumentException("Either pass in an integer ID cast to a string, or the name of a drawable resource");
        }

        public IBitmap Create(float width, float height)
        {
            return Bitmap.CreateBitmap((int)width, (int)height, Bitmap.Config.Argb8888).FromNative();
        }

        internal static Dictionary<string, int> GetDrawableList(IFullLogger log)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            return GetDrawableList(log, assemblies);
        }

        internal static Dictionary<string, int> GetDrawableList(
            IFullLogger log,
            Assembly[] assemblies)
        {
            // VS2019 onward
            var drawableTypes = assemblies
                .SelectMany(a => GetTypesFromAssembly(a, log))
                .Where(x => x.Name == "Resource" && x.GetNestedType("Drawable") != null)
                .Select(x => x.GetNestedType("Drawable"))
                .ToArray();

            if (log != null)
            {
                log.Debug("DrawableList. Got " + drawableTypes.Length + " types.");
                foreach (var drawableType in drawableTypes)
                {
                    log.Debug("DrawableList Type: " + drawableType.Name);
                }
            }

            var result = drawableTypes
                .SelectMany(x => x.GetFields())
                .Where(x => x.FieldType == typeof(int) && x.IsLiteral)
                .ToDictionary(k => k.Name, v => (int)v.GetRawConstantValue());

            if (log != null)
            {
                log.Debug("DrawableList. Got " + result.Count + " items.");
                foreach (var keyValuePair in result)
                {
                    log.Debug("DrawableList Item: " + keyValuePair.Key);
                }
            }

            return result;
        }

        internal static Type[] GetTypesFromAssembly(
            Assembly assembly,
            IFullLogger log)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // The array returned by the Types property of this exception contains a Type
                // object for each type that was loaded and null for each type that could not
                // be loaded, while the LoaderExceptions property contains an exception for
                // each type that could not be loaded.
                if (log != null)
                {
                    log.Warn("Exception while detecting drawing types.", e);

                    foreach (var loaderException in e.LoaderExceptions)
                    {
                        log.Warn("Inner Exception for detecting drawing types.", loaderException);
                    }
                }

                // null check here because mono doesn't appear to follow the MSDN documentation
                // as of July 2019.
                return e.Types != null
                    ? e.Types.Where(x => x != null).ToArray()
                    : Array.Empty<Type>();
            }
        }

        private Dictionary<string, int> GetDrawableList()
        {
            return GetDrawableList(Locator.Current.GetService<ILogManager>().GetLogger(typeof(PlatformBitmapLoader)));
        }
    }

    sealed class DrawableBitmap : IBitmap
    {
        internal Drawable inner;

        public DrawableBitmap(Drawable inner)
        {
            this.inner = inner;
        }

        public float Width
        {
            get { return (float)inner.IntrinsicWidth; }
        }

        public float Height
        {
            get { return (float)inner.IntrinsicHeight; }
        }

        public Task Save(CompressedBitmapFormat format, float quality, Stream target)
        {
            throw new NotSupportedException("You can't save resources");
        }

        public void Dispose()
        {
            var disp = Interlocked.Exchange(ref inner, null);
            if (disp != null) disp.Dispose();
        }
    }

    sealed class AndroidBitmap : IBitmap
    {
        internal Bitmap inner;
        public AndroidBitmap(Bitmap inner)
        {
            this.inner = inner;
        }

        public float Width
        {
            get { return inner.Width; }
        }

        public float Height
        {
            get { return inner.Height; }
        }

        public Task Save(CompressedBitmapFormat format, float quality, Stream target)
        {
            var fmt = format == CompressedBitmapFormat.Jpeg ? Bitmap.CompressFormat.Jpeg : Bitmap.CompressFormat.Png;
            return Task.Run(() => { inner.Compress(fmt, (int)quality * 100, target); });
        }

        public void Dispose()
        {
            var disp = Interlocked.Exchange(ref inner, null);
            if (disp != null) disp.Dispose();
        }
    }

    public static class BitmapMixins
    {
        public static Drawable ToNative(this IBitmap This)
        {
            var androidBitmap = This as AndroidBitmap;
            if (androidBitmap != null)
            {
                return new BitmapDrawable(((AndroidBitmap)This).inner);
            }
            else
            {
                return ((DrawableBitmap)This).inner;
            }
        }

        public static IBitmap FromNative(this Bitmap This, bool copy = false)
        {
            if (copy) return new AndroidBitmap(This.Copy(This.GetConfig(), true));
            return new AndroidBitmap(This);
        }

        public static IBitmap FromNative(this Drawable This)
        {
            return new DrawableBitmap(This);
        }
    }
}
