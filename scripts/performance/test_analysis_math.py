import unittest
from analysis_math import percentile,merge_histograms,rate,detect,compatible_union


class AnalysisTests(unittest.TestCase):
    def test_weighted_union_not_average_of_percentiles(self):
        first=dict(kind="histogram",unit="ms",bounds_ms=[0,1,10],buckets=[0,99,0,0],count=99,sum=99,min=1,max=1)
        second=dict(first,buckets=[0,0,1,0],count=1,sum=10,min=10,max=10)
        self.assertEqual(1,percentile(merge_histograms([first,second]),.95)["upper_ms"])
        self.assertEqual(10,percentile(second,.95)["upper_ms"])
        overflow=dict(second,buckets=[0,0,0,1])
        self.assertIsNone(percentile(overflow,.95)["upper_ms"])
        windows=[dict(instance_id="a",start_s=0,end_s=10,quality="complete",operation="operator",histogram=first),
                 dict(instance_id="b",start_s=.5,end_s=10.5,quality="complete",operation="operator",histogram=second)]
        self.assertEqual(100,compatible_union(windows,["a","b"])["count"])
        windows[1]["start_s"]=2
        self.assertIsNone(compatible_union(windows,["a","b"]))

    def test_missing_partial_and_reset(self):
        self.assertEqual(4,rate(2,.5,"partial"))
        self.assertIsNone(rate(0,10,"gap"))
        self.assertIsNone(rate(-1,10))
        self.assertIsNone(rate(2,0))

    def test_detection(self):
        windows=[dict(start_s=i*60,end_s=(i+1)*60,rate=100,clean=True,queue_sufficient=True) for i in range(10)]
        self.assertEqual("no_slowdown_detected",detect(windows)["status"])
        self.assertEqual("insufficient_data",detect(windows[:3])["status"])
        for row in windows[6:]: row["rate"]=70
        self.assertEqual("slowdown_candidate",detect(windows)["status"])
        for row in windows[6:]: row["queue_sufficient"]=False
        self.assertEqual("insufficient_data",detect(windows)["status"])

    def test_operator_delay_is_association_not_cause(self):
        windows=[dict(start_s=i*60,end_s=(i+1)*60,rate=100 if i<6 else 70,clean=True,queue_sufficient=True,
                      operator_p95={"lower_ms":1 if i<6 else 5,"upper_ms":2 if i<6 else 6}) for i in range(10)]
        result=detect(windows)
        self.assertEqual("slowdown_candidate",result["status"])
        self.assertEqual([360,420,480,540],result["operator_path_latency_increase_windows"])
        for row in windows: row["clean"]=False
        self.assertEqual("insufficient_data",detect(windows)["status"])


if __name__=="__main__": unittest.main()
