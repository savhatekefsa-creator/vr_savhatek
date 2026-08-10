using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AprilTag
{
    //
    // Job struct that wraps AprilTag pose estimator
    //
    struct PoseEstimationJob : Unity.Jobs.IJobParallelFor
    {
        // Input data struct that simply wraps pointers to tag detection data
        public struct Input
        {
            unsafe Interop.Detection* p;

            public unsafe Input(ref Interop.Detection r) =>
                p = (Interop.Detection*)Interop.Util.AsPointer(ref r);

            public unsafe ref Interop.Detection Ref => ref Interop.Util.AsRef<Interop.Detection>(p);
        }

        // I/O
        [ReadOnly]
        NativeArray<Input> _input;

        [WriteOnly]
        NativeArray<TagPose> _output;

        // Camera parameters
        double _tagSize;
        double2 _focal;         // fx, fy — AYRI tutulur
        double2 _focalCenter;   // cx, cy — goruntu merkezi DEGIL, olculen ana nokta

        //
        // FOV'dan tureten kurucu. GERI DONUK UYUMLULUK ICIN durur.
        //
        // IKI VARSAYIM YAPAR VE IKISI DE GERCEK KAMERALARDA YANLISTIR:
        //   fx == fy            -> tek bir FOV sayisindan turetilir
        //   cx,cy == goruntu/2  -> ana noktanin tam merkezde oldugu varsayilir
        //
        // Ikincisi ozellikle zararli: ana noktadaki d piksellik kayma, tag'in nereye
        // dustugune bagli olarak d/f radyanlik bir YON hatasi uretir. Kamera hareket
        // ettikce tag goruntude gezer, dolayisiyla hata da degisir — yani ORTALAMAYLA
        // BASTIRILAMAYAN, konuma bagli sistematik bir sapma. 600 px odak uzakliginda
        // 30 px'lik bir ana nokta kaymasi 2,9 derece eder; 2 metrede 10 cm yanal hata.
        //
        // Gercek intrinsics elde varsa asagidaki kurucuyu kullanin.
        //
        public PoseEstimationJob(
            NativeArray<Input> input,
            NativeArray<TagPose> output,
            int width,
            int height,
            float fov,
            float tagSize
        ) : this(input, output,
                 height / 2 / math.tan(fov / 2),   // fx
                 height / 2 / math.tan(fov / 2),   // fy
                 width / 2.0,                      // cx
                 height / 2.0,                     // cy
                 tagSize)
        {
        }

        //
        // TAM INTRINSICS kurucusu. Alt katman (estimate_tag_pose) fx, fy, cx, cy'yi zaten
        // ayri ayri kabul ediyordu; kisitlayan sey yalnizca bu sarmalayiciydi.
        //
        // DIKKAT: verilen degerler ISLENEN GORUNTUNUN cozunurlugune gore olmali. Intrinsics
        // genelde sabit bir referans cozunurluk icin tanimlanir; farkli boyutta bir doku
        // isleniyorsa fx, fy, cx, cy hepsi ayni oranla olceklenmelidir.
        //
        public PoseEstimationJob(
            NativeArray<Input> input,
            NativeArray<TagPose> output,
            double fx,
            double fy,
            double cx,
            double cy,
            float tagSize
        )
        {
            _input = input;
            _output = output;
            _tagSize = tagSize;
            _focal = math.double2(fx, fy);
            _focalCenter = math.double2(cx, cy);
        }

        // Job execution method
        public void Execute(int i)
        {
            var info = new Interop.DetectionInfo(
                ref _input[i].Ref,
                _tagSize,
                _focal.x,
                _focal.y,
                _focalCenter.x,
                _focalCenter.y
            );

            using var pose = new Interop.Pose(ref info);

            var pos = pose.t.AsFloat3() * math.float3(1, -1, 1);

            var rot = math.quaternion(pose.R.AsFloat3x3());
            rot = rot.value * math.float4(-1, 1, -1, 1);

            _output[i] = new TagPose(_input[i].Ref.ID, pos, rot);
        }
    }
} // namespace AprilTag
