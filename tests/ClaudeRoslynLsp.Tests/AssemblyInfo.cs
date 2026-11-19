using NUnit.Framework;

// A fresh instance of the test class for every test, so a constructor, a field and Dispose mean
// "per test" — NUnit's default is one instance shared by the whole fixture. Fixtures run one at a
// time, NUnit's default, which also keeps the live fixtures' real Roslyn processes from overlapping.
[assembly: FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
