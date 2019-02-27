using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Diagnostics;



namespace DisplayDetector
{

    public delegate void UpdatePictureHandler(object sender, UpdatePictureArgs e);

    public class Detector
    {
        #region リングバッファ

        class RingBuf<T>
        {
            private readonly object _asyncLock = new object();
            private readonly int _bufSize;

            private T[] _items;
            private int _bufPos = 0;
            public int Count = 0;

            [Description("インデックスは0が最新で大きくなるほど古くなる")]
            public T this[int index]
            {
                get
                {
                    if (index >= _bufSize || index >= Count)
                        throw new IndexOutOfRangeException();

                    return _items[(_bufPos + _bufSize - index - 1) % _bufSize];
                }
            }

            public RingBuf(int bufSize)
            {
                _bufSize = bufSize;
                _items = new T[_bufSize];
            }

            public void Add(T item)
            {
                lock (_asyncLock)
                {

                    _items[_bufPos] = item;
                    _bufPos++;
                    Count++;
                    if (_bufPos == _bufSize)
                        _bufPos = 0;
                }
            }

            public T Latest()
            {
                lock (_asyncLock)
                {
                    return _items[_bufPos];
                }
            }

            [Description("インデックスは0が最新で大きくなるほど古くなる")]
            public T[] ToArray()
            {
                T[] items;
                lock (_asyncLock)
                {

                    int num = _bufSize < Count ? _bufSize : Count;
                    items = new T[num];
                    for (int i = 0; i < num; i++)
                    {
                        items[i] = _items[(_bufPos + _bufSize - i) / _bufSize];
                    }
                }
                return items;
            }

            public void Clear()
            {
                lock (_asyncLock)
                {
                    Count = 0;
                    _bufPos = 0;
                    _items = new T[_bufSize];
                }
            }

        }
        #endregion

        #region private変数



        //キャプチャ画面の情報
        private IntPtr _hwnd;//ウィンドウハンドル
        private BITMAP _defBitmap;//デフォルトのビットマップ情報

        //検出器の情報
        private bool _isRun = false; //検出器が起動しているかどうか


        internal Exception _exception { get; private set; } //例外が発生しているかどうか

        private Stopwatch _sw = new Stopwatch();
        private long _divTime;
        private object _lapTimeLock = new object();

        #endregion

        #region privateメソッド
        //初期化
        void Initialize(PixelFormat pixelFormat)
        {

            _defBitmap = DisplayCapt.CaptureWindow(_hwnd);

            if (_defBitmap == null)
                throw new InvalidHandleException();

            _defBitmap.PixelFormat = pixelFormat;
            BitmapData bitmapData = _defBitmap.Bitmap.LockBits(new Rectangle(0, 0, _defBitmap.Width, _defBitmap.Height), ImageLockMode.ReadOnly, _defBitmap.PixelFormat);
            _defBitmap.Stride = bitmapData.Stride;
            _defBitmap.Size = _defBitmap.Stride * _defBitmap.Height;
            _defBitmap.Bitmap.UnlockBits(bitmapData);

        }

        //一定時間ごとにスクショを保存する。
        void GetPict()
        {
            BITMAP bitmap = DisplayCapt.CaptureWindow(_hwnd);
            if (bitmap == null)
            {
                _exception = new InvalidHandleException();
                _isRun = false;
                return;
            }

            if (bitmap.Width != _defBitmap.Width || bitmap.Height != _defBitmap.Height)
            {
                _exception = new ChangeSizeException();
                _isRun = false;
                return;
            }


            BitmapData bdata = bitmap.Bitmap.LockBits(new Rectangle(0, 0, _defBitmap.Width, _defBitmap.Height), ImageLockMode.ReadOnly, _defBitmap.PixelFormat);

            byte[] buf = new byte[_defBitmap.Size];

            Marshal.Copy(bdata.Scan0, buf, 0, _defBitmap.Size);

            bitmap.Bitmap.UnlockBits(bdata);

            _pictureBuf.Add(buf);

        }

        //検出器の起動中のメソッド
        async void Running()
        {
            await Task.Run(() =>
            {

                _sw.Start();
                long formerTime = 0;
                while (_isRun)
                {
                    try
                    {


                        GetPict();
                        Detection();

                        long time;
                        do
                        {
                            time = _sw.ElapsedMilliseconds;
                        } while (time - formerTime < 200);

                        lock (_lapTimeLock)
                        {
                            _divTime = time - formerTime;
                        }
                        formerTime = time;

                        UpdatePictureCall(this, new UpdatePictureArgs(this));
                    }
                    catch (Exception e)
                    {
                        _isRun = false;
                        _exception = e;
                    }
                }
                _sw.Stop();
            });
        }

        #endregion

        #region 検出器プログラム

        //HSV構造体
        struct HSV
        {
            public readonly static uint uintMax = uint.MaxValue;

            public float H;//0~360
            public float S;//0~uintMax
            public float V;//0~uintMax

            public HSV(float h, float s, float v)
            {
                H = h;
                S = s;
                V = v;
            }

            public static HSV BGRtoHSV(byte b, byte g, byte r)
            {
                float[] bgr = new float[] { b, g, r };
                int maxI; //rgb最大
                int minI; //rgb最小
                float Hh, Ss, Vv;

                #region 最大最小の割り当て
                if (bgr[0] >= bgr[1])
                    if (bgr[0] >= bgr[2])
                    {
                        maxI = 0;
                        if (bgr[1] >= bgr[2])
                        {
                            if (bgr[0] == bgr[2])
                                minI = 0;
                            else
                                minI = 2;
                        }
                        else
                            minI = 1;
                    }
                    else
                    {
                        minI = 1;
                        maxI = 2;
                    }
                else if (bgr[1] >= bgr[2])
                {
                    maxI = 1;
                    if (bgr[0] >= bgr[2])
                        minI = 2;
                    else
                        minI = 0;
                }
                else
                {
                    minI = 0;
                    maxI = 2;
                }
                #endregion

                #region SとV

                if (bgr[maxI] != 0)
                    Ss = (bgr[maxI] - bgr[minI]) / bgr[maxI];
                else
                    Ss = 0;
                Vv = bgr[maxI] / 255;

                #endregion

                #region H

                if (maxI != minI)
                {
                    float h = 0;
                    switch (maxI)
                    {
                        case 0:
                            h = 60 * (bgr[1] - bgr[2]) / (bgr[maxI] - bgr[minI]);
                            break;
                        case 1:
                            h = 60 * (bgr[2] - bgr[0]) / (bgr[maxI] - bgr[minI]) + 120;
                            break;
                        case 2:
                            h = 60 * (bgr[0] - bgr[1]) / (bgr[maxI] - bgr[minI]) + 240;
                            break;
                    }
                    Hh = (h + 360) % 360;
                }
                else
                {

                    Hh = -1;
                }

                #endregion

                return new HSV(Hh, Ss, Vv);
            }

            public byte[] ToBGR()
            {
                float max = V * 255;
                float min = (V - S * V) * 255;

                if (S == 0)
                {
                    return new[] { (byte)min, (byte)min, (byte)min };
                }
                else if (H < 60)
                {

                    return new byte[] { (byte)max, (byte)((H / 60.0) * (max - min) + min), (byte)min };
                }
                else if (H < 120)
                {

                    return new byte[] { (byte)(((120 - H) / 60.0) * (max - min) + min), (byte)max, (byte)min };
                }
                else if (H < 180)
                {

                    return new byte[] { (byte)min, (byte)max, (byte)(((H - 120) / 60.0) * (max - min) + min) };
                }
                else if (H < 240)
                {

                    return new byte[] { (byte)min, (byte)(((240 - H) / 60.0) * (max - min) + min), (byte)max };
                }
                else if (H < 300)
                {

                    return new byte[] { (byte)(((H - 240) / 60.0) * (max - min) + min), (byte)min, (byte)max };
                }

                return new byte[] { (byte)max, (byte)min, (byte)(((360 - H) / 60.0) * (max - min) + min) };

            }
        }

        RingBuf<byte[]> _pictureBuf = new RingBuf<byte[]>(5);//キャプチャ画面のリングバッファ
        private RingBuf<byte[,]> _divs = new RingBuf<byte[,]>(2);//
        private RingBuf<HSV[,]> _hsvs = new RingBuf<HSV[,]>(10);
        private byte[][,] _marks = new byte[5][,];
        private byte[] OutputPicture;//出力する画像

        //一定時間ごとに解析
        void Detection()
        {
            if (_pictureBuf.Count < 2)
                return;

            int height = _defBitmap.Height;
            int width = _defBitmap.Width;
            int stride = _defBitmap.Stride;

            byte[] pic1 = _pictureBuf[0];

            HSV[,] picHsv = new HSV[width, height];

            //hsv作成
            for (int i = 0; i < height; i++)
            {
                int h = i * stride;
                for (int j = 0; j < width; j++)
                {
                    picHsv[j, i] = HSV.BGRtoHSV(pic1[4 * j + h], pic1[1 + 4 * j + h], pic1[2 + 4 * j + h]);
                }
            }
            _hsvs.Add(picHsv);

            //検出器の実行
            Detector1(width, height, stride, ref pic1);
            //Detector2(width, height, stride, ref pic1);

            OutputPicture = pic1;
        }

        #region 検出器1と2

        void Detector1(int width, int height, int stride, ref byte[] pic)
        {
            //一定数の画像がなければ実行しない
            if (_hsvs.Count < 3)
                return;

            //表示する画像を設定
            pic = _pictureBuf[1];

            //検出処理
            for (int i = 0; i < height; i++)
            {
                int h = i * stride;
                for (int j = 0; j < width; j++)
                {
                    HSV hsv0 = _hsvs[1][j, i];
                    HSV hsv1 = _hsvs[0][j, i];
                    float hdiv = hsv0.H - hsv1.H;
                    
                    if (((hdiv > 2 || hdiv < -2) && hsv0.V >= 0.2 && hsv1.V >= 0.2 && hsv0.S >= 0.1 && hsv1.S >= 0.1)
                        || (hsv0.V - 0.2) * (hsv1.V - 0.2) < 0
                        || (hsv0.S - 0.1) * (hsv1.S - 0.1) < 0)
                    {

                        hsv1 = _hsvs[2][j, i];
                        hdiv = hsv0.H - hsv1.H;

                        if (((hdiv > 2 || hdiv < -2) && hsv0.V >= 0.2 && hsv1.V >= 0.2 && hsv0.S >= 0.1 && hsv1.S >= 0.1)
                            || (hsv0.V - 0.2) * (hsv1.V - 0.2) < 0
                            || (hsv0.S - 0.1) * (hsv1.S - 0.1) < 0)
                        {
                            //検出している部分ならそのままにする
                            continue;
                        }
                    }

                    //検出していない部分を黒く塗りつぶす
                    pic[4 * j + h] = 0;
                    pic[1 + 4 * j + h] = 0;
                    pic[2 + 4 * j + h] = 0;

                }
            }

            
        }

        void Detector2(int width, int height, int stride, ref byte[] pic)
        {
            //一定数の画像がなければ実行しない
            if (_hsvs.Count < 9)
                return;

            //rgb変換
            pic = _pictureBuf[4];

            for (int i = 0; i < height; i++)
            {
                int h = i * stride;

                for (int j = 0; j < width; j++)
                {
                    HSV hsv0 = _hsvs[4][j, i];
                    bool flag = false;

                    for (int k = 0; k < 4; k++)
                    {
                        
                        HSV hsv1 = _hsvs[k][j, i];
                        float hdiv = hsv0.H - hsv1.H;
                        if (((hdiv > 2 || hdiv < -2) && hsv0.V >= 0.2 && hsv1.V >= 0.2 && hsv0.S >= 0.1 && hsv1.S >= 0.1)
                            || (hsv0.V - 0.2) * (hsv1.V - 0.2) < 0
                            || (hsv0.S - 0.1) * (hsv1.S - 0.1) < 0)
                        {
                            hsv1 = _hsvs[8 - k][j, i];
                            hdiv = hsv0.H - hsv1.H;
                            if (((hdiv > 2 || hdiv < -2) && hsv0.V >= 0.2 && hsv1.V >= 0.2 && hsv0.S >= 0.1 && hsv1.S >= 0.1)
                                || (hsv0.V - 0.2) * (hsv1.V - 0.2) < 0
                                || (hsv0.S - 0.1) * (hsv1.S - 0.1) < 0)
                            {
                                //検出しているならフラグを立てる。
                                flag = true;
                                break;
                            }
                        }

                    }
                    //フラグが立ってなかったら（検出してなかったら）黒く塗りつぶす
                    if (!flag)
                    {
                        pic[4 * j + h] = 0;
                        pic[1 + 4 * j + h] = 0;
                        pic[2 + 4 * j + h] = 0;
                    }
                }
            }

            


        }
        #endregion

        #endregion

        #region publicメンバー



        //画像情報のプロパティ
        public int Height => _defBitmap.Height;
        public int Width => _defBitmap.Width;
        public int Stride => _defBitmap.Stride;

        //コンストラクタ
        public Detector(IntPtr hWnd, PixelFormat pixelFormat)
        {
            _hwnd = hWnd;//ハンドルを取得

            Initialize(pixelFormat);//初期化
        }

        //探索の開始
        public void Start()
        {
            if (_exception != null)
                throw _exception;

            if (_isRun)
                return;

            _isRun = true;
            Running();
        }

        //探索の終了
        public void Stop()
        {
            //if (_exception != null)
            //  throw _exception;

            _isRun = false;
        }

        //画像の取得
        public byte[] GetPicture()
        {
            if (_exception != null)
                throw _exception;

            if (!_isRun)
                return null;
            return OutputPicture;
        }

        //フレームレートの取得
        public double GetFramerate()
        {
            lock (_lapTimeLock)
            {
                return 1000.0 / _divTime;
            }
        }


        public event UpdatePictureHandler UpdatePictureCall = delegate { };

        #endregion
    }

    public class UpdatePictureArgs
    {
        internal UpdatePictureArgs(Detector d)
        {
            try
            {
                Bitmap = d.GetPicture();
                FrameRate = d.GetFramerate();
                Width = d.Width;
                Height = d.Height;
                Stride = d.Stride;
                Exception = d._exception;
            }
            catch (Exception e)
            {
                Exception = e;
            }
        }

        public byte[] Bitmap { get; }
        public double FrameRate { get; }
        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public Exception Exception;
    }



    //キャプチャ画面のサイズが変わったときに起こる例外
    public class ChangeSizeException : Exception
    {

    }

    //キャプチャ画面のハンドルが不正であるときに起こる例外
    public class InvalidHandleException : Exception
    {

    }

}
