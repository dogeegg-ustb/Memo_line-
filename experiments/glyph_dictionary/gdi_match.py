import ctypes as C, ctypes.wintypes as W, json, sys, time, statistics
import numpy as np, cv2
g=C.windll.gdi32; u=C.windll.user32
class BMIH(C.Structure):
    _fields_=[('biSize',W.DWORD),('biWidth',W.LONG),('biHeight',W.LONG),('biPlanes',W.WORD),('biBitCount',W.WORD),
              ('biCompression',W.DWORD),('biSizeImage',W.DWORD),('biXPelsPerMeter',W.LONG),('biYPelsPerMeter',W.LONG),('biClrUsed',W.DWORD),('biClrImportant',W.DWORD)]
class SIZE(C.Structure): _fields_=[('cx',W.LONG),('cy',W.LONG)]
g.CreateFontW.restype=W.HANDLE; g.CreateCompatibleDC.restype=W.HANDLE; g.CreateDIBSection.restype=W.HANDLE
g.SelectObject.argtypes=[W.HANDLE,W.HANDLE]; g.SelectObject.restype=W.HANDLE
g.DeleteObject.argtypes=[W.HANDLE]
g.GetTextExtentPoint32W.argtypes=[W.HANDLE,W.LPCWSTR,C.c_int,C.c_void_p]
g.TextOutW.argtypes=[W.HANDLE,C.c_int,C.c_int,W.LPCWSTR,C.c_int]
g.SetBkColor.argtypes=[W.HANDLE,W.DWORD]; g.SetTextColor.argtypes=[W.HANDLE,W.DWORD]; g.SetBkMode.argtypes=[W.HANDLE,C.c_int]
g.CreateDIBSection.argtypes=[W.HANDLE,C.c_void_p,W.UINT,C.c_void_p,W.HANDLE,W.DWORD]
g.CreateCompatibleDC.argtypes=[W.HANDLE]
dc=g.CreateCompatibleDC(None)
_fonts={}
def font(face,px,quality):
    k=(face,px,quality)
    if k not in _fonts: _fonts[k]=g.CreateFontW(-px,0,0,0,400,0,0,0,1,0,0,quality,0,face)
    return _fonts[k]
def render(text,face,px,quality=4):  # 4=ANTIALIASED, 3=NONANTIALIASED, 5=CLEARTYPE
    g.SelectObject(dc,font(face,px,quality))
    s=SIZE(); g.GetTextExtentPoint32W(dc,text,len(text),C.byref(s))
    w,h=s.cx+8,s.cy+8
    bi=BMIH(C.sizeof(BMIH),w,-h,1,32,0,0,0,0,0,0); bits=C.c_void_p()
    bmp=g.CreateDIBSection(dc,C.byref(bi),0,C.byref(bits),None,0)
    old=g.SelectObject(dc,bmp)
    g.SetBkColor(dc,0); g.SetTextColor(dc,0xFFFFFF); g.SetBkMode(dc,2)
    g.TextOutW(dc,4,4,text,len(text))
    a=np.ctypeslib.as_array(C.cast(bits,C.POINTER(C.c_uint8)),(h,w,4)).copy()
    g.SelectObject(dc,old); g.DeleteObject(bmp)
    im=a[:,:,:3].max(axis=2)
    ys,xs=np.where(im>40)
    return im[ys.min():ys.max()+1,xs.min():xs.max()+1] if len(xs) else im
def prep(crop):
    gr=cv2.cvtColor(crop,cv2.COLOR_BGR2GRAY).astype(np.float32)
    edge=np.concatenate([gr[0],gr[-1],gr[:,0],gr[:,-1]])
    bg=np.median(edge)
    return np.abs(gr-bg)  # text-vs-background magnitude, polarity-free
def score(crop_p,tpl):
    if tpl.shape[0]>crop_p.shape[0] or tpl.shape[1]>crop_p.shape[1]: return -1.0
    return float(cv2.matchTemplate(crop_p,tpl.astype(np.float32),cv2.TM_CCOEFF_NORMED).max())
