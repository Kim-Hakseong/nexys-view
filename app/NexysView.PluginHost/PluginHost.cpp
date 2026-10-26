// DataPlugin mini host for the public the vendor C++ DataPlugin SDK ABI (USP_TDM_MARSHALLER -> IRoot / IRootExport).
// Independent of the reference tool: loads a plugin DLL, drives it through the SDK interfaces and dumps the result as JSON.
// Based on the SDK's tests/simple_read_test.cpp (MIT). Headers: ref/dataplugin-sdk-cpp/include.
//   dp_minihost read   <plugin.dll> <datafile> <out.json> [param-xml]   (default param: <filename readonly="yes">datafile</filename>)
//   dp_minihost export <plugin.dll> <outfile>  <param-xml> [init-param-xml]  (exports a fixed in-memory test tree)
#include <windows.h>
#include <comutil.h>
#include <comdef.h>
#include <cstdio>
#include <string>
#include <vector>
#include <ni/dataplugin/IRoot.h>
#include <ni/dataplugin/IRootExport.h>
#pragma comment(lib, "comsuppw.lib")
#pragma comment(lib, "oleaut32.lib")

using namespace ni::dataplugin;
typedef HRESULT(__cdecl* tMarshaller)(REFIID, void**);

static FILE* g_out = nullptr;
static FILE* g_log = stderr;

static std::string U8(const wchar_t* w)
{
  if (!w) return std::string();
  int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
  std::string s(n > 0 ? n - 1 : 0, '\0');
  if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, &s[0], n, nullptr, nullptr);
  return s;
}

static std::string J(const std::string& s)
{
  std::string o = "\"";
  for (unsigned char c : s) {
    if (c == '"') o += "\\\"";
    else if (c == '\\') o += "\\\\";
    else if (c == '\n') o += "\\n";
    else if (c == '\r') o += "\\r";
    else if (c == '\t') o += "\\t";
    else if (c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); o += b; }
    else o += (char)c;
  }
  return o + "\"";
}

static std::string Num(double d)
{
  if (d != d) return "\"NaN\"";
  if (d == HUGE_VAL) return "\"Inf\"";
  if (d == -HUGE_VAL) return "\"-Inf\"";
  char b[40];
  sprintf_s(b, "%.17g", d);
  return b;
}

static std::string Scalar(const VARIANT& v)
{
  switch (v.vt) {
  case VT_EMPTY: case VT_NULL: return "null";
  case VT_BSTR: return J(U8(v.bstrVal));
  case VT_R8: return Num(v.dblVal);
  case VT_R4: return Num(v.fltVal);
  case VT_DATE: return "{\"date\":" + Num(v.date) + "}";
  case VT_BOOL: return v.boolVal ? "true" : "false";
  case VT_I1: return std::to_string(v.cVal);
  case VT_UI1: return std::to_string(v.bVal);
  case VT_I2: return std::to_string(v.iVal);
  case VT_UI2: return std::to_string(v.uiVal);
  case VT_I4: case VT_INT: return std::to_string(v.lVal);
  case VT_UI4: case VT_UINT: return std::to_string(v.ulVal);
  case VT_I8: return std::to_string(v.llVal);
  case VT_UI8: return std::to_string(v.ullVal);
  default: {
    _variant_t t;
    if (SUCCEEDED(VariantChangeType(&t, &v, 0, VT_BSTR))) return "{\"vt\":" + std::to_string(v.vt) + ",\"s\":" + J(U8(t.bstrVal)) + "}";
    return "{\"vt\":" + std::to_string(v.vt) + "}";
  }
  }
}

// values VARIANT: SAFEARRAY of a scalar type (or VARIANT array) -> JSON items appended to out; returns count
static size_t Values(const VARIANT& v, std::string& out, bool& first)
{
  if (v.vt == VT_EMPTY) return 0;
  if (!(v.vt & VT_ARRAY)) { out += (first ? "" : ",") + Scalar(v); first = false; return 1; }
  SAFEARRAY* sa = (v.vt & VT_BYREF) ? *v.pparray : v.parray;
  if (!sa) return 0;
  LONG lo = 0, hi = -1;
  SafeArrayGetLBound(sa, 1, &lo);
  SafeArrayGetUBound(sa, 1, &hi);
  VARTYPE et = (VARTYPE)(v.vt & VT_TYPEMASK);
  size_t n = 0;
  for (LONG i = lo; i <= hi; ++i) {
    VARIANT e; VariantInit(&e);
    if (et == VT_VARIANT) { SafeArrayGetElement(sa, &i, &e); }
    else {
      e.vt = et;
      if (et == VT_BSTR) { BSTR b = nullptr; SafeArrayGetElement(sa, &i, &b); e.bstrVal = b; }
      else SafeArrayGetElement(sa, &i, &e.llVal);
    }
    out += (first ? "" : ",") + Scalar(e);
    first = false;
    VariantClear(&e);
    ++n;
  }
  return n;
}

static std::string Props(const IProperties* p)
{
  std::string o = "{";
  ULONG n = 0;
  if (FAILED(p->CountCustomProperties(n))) return "{\"_error\":\"CountCustomProperties\"}";
  for (ULONG i = 0; i < n; ++i) {
    BSTR name = nullptr; VARIANT val; VariantInit(&val);
    HRESULT hr = p->GetCustomProperty(i, name, val);
    if (i) o += ",";
    std::string sv;
    if (SUCCEEDED(hr)) sv = Scalar(val);
    else sv = "\"hr " + std::to_string(hr) + "\"";
    o += J(U8(name)) + ":" + sv;
    SysFreeString(name); VariantClear(&val);
  }
  return o + "}";
}

static std::string Bstr(HRESULT hr, BSTR b) { std::string s = SUCCEEDED(hr) ? J(U8(b)) : "null"; SysFreeString(b); return s; }

static HMODULE Load(const wchar_t* dll, tMarshaller& m)
{
  HMODULE h = LoadLibraryExW(dll, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
  if (!h) { fwprintf(g_log, L"ERROR LoadLibrary %s gle=%lu\n", dll, GetLastError()); return nullptr; }
  m = (tMarshaller)GetProcAddress(h, "USP_TDM_MARSHALLER");
  if (!m) { fwprintf(g_log, L"ERROR no USP_TDM_MARSHALLER in %s\n", dll); return nullptr; }
  return h;
}

static int DoRead(const wchar_t* dll, const wchar_t* file, const wchar_t* outPath, const wchar_t* param)
{
  tMarshaller m = nullptr;
  if (!Load(dll, m)) return 2;
  void* p = nullptr;
  HRESULT hr = m(IID_IRoot, &p);
  fwprintf(g_log, L"USP_TDM_MARSHALLER(IID_IRoot) hr=0x%08lx ptr=%p\n", (unsigned long)hr, p);
  if (!p) return 3;
  IRoot* root = static_cast<IRoot*>(p);
  std::wstring ps = param ? std::wstring(param) : (std::wstring(L"<filename readonly=\"yes\">") + file + L"</filename>");
  hr = root->SetParameter(_bstr_t(ps.c_str()).GetBSTR());
  fwprintf(g_log, L"SetParameter hr=0x%08lx\n", (unsigned long)hr);
  _bstr_t bf(file);
  hr = root->Initialize(bf.GetBSTR());
  fwprintf(g_log, L"Initialize hr=0x%08lx\n", (unsigned long)hr);
  if (FAILED(hr)) { root->Release(); return 4; }
  if (_wfopen_s(&g_out, outPath, L"wb") != 0) return 5;
  std::string o = "{";
  BSTR b = nullptr;
  hr = root->GetName(b); o += "\"name\":" + Bstr(hr, b); b = nullptr;
  hr = root->GetDescription(b); o += ",\"description\":" + Bstr(hr, b); b = nullptr;
  hr = root->GetTitle(b); o += ",\"title\":" + Bstr(hr, b); b = nullptr;
  hr = root->GetAuthor(b); o += ",\"author\":" + Bstr(hr, b); b = nullptr;
  DATE d = 0; hr = root->GetDate(d); o += ",\"date\":" + (SUCCEEDED(hr) ? Num(d) : std::string("null"));
  o += ",\"props\":" + Props(root) + ",\"groups\":[";
  fwrite(o.data(), 1, o.size(), g_out); o.clear();
  ULONG ng = 0; root->GetNumberOfGroups(ng);
  for (ULONG gi = 0; gi < ng; ++gi) {
    IGroup* g = nullptr;
    if (FAILED(root->GetGroup(gi, g)) || !g) continue;
    o = gi ? "," : "";
    b = nullptr; hr = g->GetName(b); o += "{\"name\":" + Bstr(hr, b); b = nullptr;
    hr = g->GetDescription(b); o += ",\"description\":" + Bstr(hr, b);
    o += ",\"props\":" + Props(g) + ",\"channels\":[";
    ULONG nc = 0; g->GetNumberOfChannels(nc);
    for (ULONG ci = 0; ci < nc; ++ci) {
      IChannel* c = nullptr;
      if (FAILED(g->GetChannel(ci, c)) || !c) continue;
      o += ci ? "," : "";
      b = nullptr; hr = c->GetName(b); o += "{\"name\":" + Bstr(hr, b); b = nullptr;
      hr = c->GetDescription(b); o += ",\"description\":" + Bstr(hr, b); b = nullptr;
      hr = c->GetUnit(b); o += ",\"unit\":" + Bstr(hr, b);
      VARTYPE vt = 0; c->GetDataType(vt); o += ",\"vt\":" + std::to_string(vt);
      ULONG64 len = 0; c->GetLength(len); o += ",\"length\":" + std::to_string(len);
      ULONG imp = 0; c->IsImplicit(imp); o += ",\"implicit\":" + std::to_string(imp);
      ULONG rl = 0; double off = 0, fac = 1; c->IsRawLinear(rl, off, fac);
      o += ",\"rawlinear\":[" + std::to_string(rl) + "," + Num(off) + "," + Num(fac) + "]";
      double mn = 0, mx = 0; HRESULT h1 = c->GetMinimum(mn), h2 = c->GetMaximum(mx);
      o += ",\"min\":" + (h1 == S_OK ? Num(mn) : std::string("null")) + ",\"max\":" + (h2 == S_OK ? Num(mx) : std::string("null"));
      o += ",\"props\":" + Props(c) + ",\"values\":[";
      fwrite(o.data(), 1, o.size(), g_out); o.clear();
      bool first = true;
      ULONG64 got = 0;
      const ULONG chunk = 65536;
      while (got < len) {
        VARIANT v; VariantInit(&v);
        ULONG take = (ULONG)min((ULONG64)chunk, len - got);
        hr = c->GetValues(got, take, v);
        if (FAILED(hr)) { fwprintf(g_log, L"GetValues failed g%lu c%lu at %llu hr=0x%08lx\n", gi, ci, got, (unsigned long)hr); break; }
        std::string vs;
        size_t n = Values(v, vs, first);
        VariantClear(&v);
        fwrite(vs.data(), 1, vs.size(), g_out);
        if (n == 0) break;
        got += n;
      }
      o = "],\"values_read\":" + std::to_string(got);
      ULONG64 fl = 0;
      if (SUCCEEDED(c->GetFlagsLength(fl)) && fl > 0) {   // invalid-value flags (semantics measured, not documented)
        o += ",\"flags_length\":" + std::to_string(fl) + ",\"flags\":[";
        fwrite(o.data(), 1, o.size(), g_out); o.clear();
        bool ff = true;
        ULONG64 fg = 0;
        while (fg < fl) {
          VARIANT v; VariantInit(&v);
          ULONG take = (ULONG)min((ULONG64)65536, fl - fg);
          if (FAILED(c->GetFlags(fg, take, v))) break;
          std::string vs;
          size_t n = Values(v, vs, ff);
          VariantClear(&v);
          fwrite(vs.data(), 1, vs.size(), g_out);
          if (n == 0) break;
          fg += n;
        }
        o = "]";
      }
      o += "}";
      c->Release();
    }
    o += "]}";
    fwrite(o.data(), 1, o.size(), g_out); o.clear();
    g->Release();
  }
  o = "]}\n";
  fwrite(o.data(), 1, o.size(), g_out);
  fclose(g_out);
  root->Release();
  fwprintf(g_log, L"read ok groups=%lu\n", ng);
  return 0;
}

// ---------------------------------------------------------------- export: in-memory tree implementing the SDK interfaces
template <class I> class RefBase : public I {
  LONG ref_ = 1;
public:
  virtual ~RefBase() {}
  HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** p) override { *p = nullptr; return E_NOINTERFACE; }
  ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref_); }
  ULONG STDMETHODCALLTYPE Release() override { LONG r = InterlockedDecrement(&ref_); if (!r) delete this; return r; }
};

struct MemChannel : RefBase<IChannel> {
  std::wstring name, unit, desc; VARTYPE vt; std::vector<double> d; std::vector<std::wstring> s;
  HRESULT GetName(BSTR& v) const override { v = SysAllocString(name.c_str()); return S_OK; }
  HRESULT GetDescription(BSTR& v) const override { v = SysAllocString(desc.c_str()); return desc.empty() ? S_FALSE : S_OK; }
  HRESULT CountCustomProperties(ULONG& c) const override { c = 0; return S_OK; }
  HRESULT GetCustomProperty(ULONG, BSTR&, VARIANT&) const override { return S_FALSE; }
  HRESULT GetLength(ULONG64& v) const override { v = vt == VT_BSTR ? s.size() : d.size(); return S_OK; }
  HRESULT GetDataType(VARTYPE& v) const override { v = vt; return S_OK; }
  HRESULT GetUnit(BSTR& v) const override { v = SysAllocString(unit.c_str()); return unit.empty() ? S_FALSE : S_OK; }
  HRESULT GetMinimum(DOUBLE&) const override { return S_FALSE; }
  HRESULT GetMaximum(DOUBLE&) const override { return S_FALSE; }
  HRESULT GetValues(ULONG64 skip, ULONG take, VARIANT& v) const override {
    ULONG64 n = vt == VT_BSTR ? s.size() : d.size();
    VariantInit(&v);
    if (skip > n) return E_INVALIDARG;
    ULONG cnt = (ULONG)min((ULONG64)take, n - skip);
    if (cnt == 0) return S_OK;
    SAFEARRAY* sa = SafeArrayCreateVector(vt, 0, cnt);
    for (LONG i = 0; i < (LONG)cnt; ++i) {
      if (vt == VT_BSTR) { BSTR b = SysAllocString(s[(size_t)skip + i].c_str()); SafeArrayPutElement(sa, &i, b); SysFreeString(b); }
      else if (vt == VT_I4) { LONG x = (LONG)d[(size_t)skip + i]; SafeArrayPutElement(sa, &i, &x); }
      else { double x = d[(size_t)skip + i]; SafeArrayPutElement(sa, &i, &x); }
    }
    v.vt = VT_ARRAY | vt; v.parray = sa;
    return S_OK;
  }
};

struct MemGroup : RefBase<IGroup> {
  std::wstring name, desc; std::vector<MemChannel*> ch;
  ~MemGroup() { for (auto c : ch) c->Release(); }
  HRESULT GetName(BSTR& v) const override { v = SysAllocString(name.c_str()); return S_OK; }
  HRESULT GetDescription(BSTR& v) const override { v = SysAllocString(desc.c_str()); return S_OK; }
  HRESULT CountCustomProperties(ULONG& c) const override { c = 0; return S_OK; }
  HRESULT GetCustomProperty(ULONG, BSTR&, VARIANT&) const override { return S_FALSE; }
  HRESULT GetNumberOfChannels(ULONG& c) const override { c = (ULONG)ch.size(); return S_OK; }
  HRESULT GetChannel(ULONG i, IChannel*& c) const override { if (i >= ch.size()) return E_INVALIDARG; c = ch[i]; c->AddRef(); return S_OK; }
};

struct MemRoot : RefBase<IRoot> {
  std::vector<MemGroup*> g;
  ~MemRoot() { for (auto x : g) x->Release(); }
  HRESULT GetName(BSTR& v) const override { v = SysAllocString(L"minihost_export"); return S_OK; }
  HRESULT GetDescription(BSTR& v) const override { v = SysAllocString(L"export test tree"); return S_OK; }
  HRESULT CountCustomProperties(ULONG& c) const override { c = 0; return S_OK; }
  HRESULT GetCustomProperty(ULONG, BSTR&, VARIANT&) const override { return S_FALSE; }
  HRESULT Initialize(const BSTR&) override { return S_OK; }
  HRESULT GetNumberOfGroups(ULONG& c) const override { c = (ULONG)g.size(); return S_OK; }
  HRESULT GetGroup(ULONG i, IGroup*& x) override { if (i >= g.size()) return E_INVALIDARG; x = g[i]; x->AddRef(); return S_OK; }
  HRESULT GetTitle(BSTR& v) const override { v = SysAllocString(L"title"); return S_OK; }
  HRESULT GetAuthor(BSTR& v) const override { v = SysAllocString(L"dp_minihost"); return S_OK; }
  HRESULT GetDate(DATE& d) const override { d = 45000.5; return S_OK; }
};

// fixed test tree (same values are created in 기준 도구 by work/reference_scripts/export_ref.vbs)
static MemRoot* BuildTree()
{
  MemRoot* r = new MemRoot();
  MemGroup* g = new MemGroup(); g->name = L"G1"; g->desc = L"group one";
  MemChannel* t = new MemChannel(); t->name = L"Time"; t->unit = L"s"; t->vt = VT_R8;
  MemChannel* y = new MemChannel(); y->name = L"Signal"; y->unit = L"V"; y->vt = VT_R8;
  MemChannel* k = new MemChannel(); k->name = L"Count"; k->vt = VT_R8;
  for (int i = 0; i < 20; ++i) {
    t->d.push_back(i * 0.125);
    y->d.push_back(1.0 / (i + 1) - 0.25 * i);
    k->d.push_back(i * 7 - 50);
  }
  g->ch = { t, y, k };
  r->g = { g };
  return r;
}

static int DoExport(const wchar_t* dll, const wchar_t* outFile, const wchar_t* param, const wchar_t* init)
{
  tMarshaller m = nullptr;
  if (!Load(dll, m)) return 2;
  void* p = nullptr;
  HRESULT hr = m(IID_IRootExport, &p);
  fwprintf(g_log, L"USP_TDM_MARSHALLER(IID_IRootExport) hr=0x%08lx ptr=%p\n", (unsigned long)hr, p);
  if (!p) return 3;
  IRootExport* ex = static_cast<IRootExport*>(p);
  MemRoot* root = BuildTree();
  std::wstring ps(param);
  size_t at = ps.find(L"@OUT@");
  if (at != std::wstring::npos) ps.replace(at, 5, outFile);
  BSTR err = nullptr;
  _bstr_t bp(ps.c_str()), bi(init ? init : L"");
  hr = ex->Execute(bp.GetBSTR(), bi.GetBSTR(), root, err);
  fwprintf(g_log, L"Execute hr=0x%08lx err=%s\n", (unsigned long)hr, err ? err : L"");
  SysFreeString(err);
  root->Release();
  ex->Release();
  return FAILED(hr) ? 4 : 0;
}

int wmain(int argc, wchar_t** argv)
{
  CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  int rc = 1;
  if (argc >= 5 && !wcscmp(argv[1], L"read")) rc = DoRead(argv[2], argv[3], argv[4], argc > 5 ? argv[5] : nullptr);
  else if (argc >= 5 && !wcscmp(argv[1], L"export")) rc = DoExport(argv[2], argv[3], argv[4], argc > 5 ? argv[5] : nullptr);
  else fwprintf(stderr, L"usage: dp_minihost read <dll> <file> <out.json> [param] | export <dll> <outfile> <param> [init]\n");
  CoUninitialize();
  return rc;
}
