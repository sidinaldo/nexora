import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AuthServico, CHAVE_TOKEN, CHAVE_USUARIO } from '../servicos/auth.servico';
import { ThrottleLogin } from './throttle-login';
import { interceptorToken } from './interceptor-token';

/** O interceptor é o único ponto por onde passa TODA requisição autenticada do painel. Um erro
 *  aqui não aparece numa tela — aparece em todas, e do jeito mais confuso possível: sessão que
 *  cai sozinha, ou token indo junto num fluxo público e a API recusando por expiração. */
describe('interceptorToken', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let auth: AuthServico;
  let throttle: ThrottleLogin;
  let navegou: string[][];

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(withInterceptors([interceptorToken])),
        provideHttpClientTesting(),
        {
          // Router de verdade puxaria configuração de rotas e mudaria a URL do runner.
          // O que importa aqui é PARA ONDE o interceptor manda, não a navegação em si.
          provide: Router,
          useValue: { navigate: (destino: string[]) => { navegou.push(destino); } }
        }
      ]
    });

    navegou = [];
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthServico);
    throttle = TestBed.inject(ThrottleLogin);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  function comSessao() {
    auth.aplicarLogin({
      token: 'token-de-teste',
      usuario: { id: 1, nome: 'Ana', email: 'ana@x.com', papel: 'dono', empresaNome: 'X' }
    } as never);
  }

  describe('anexar o token', () => {
    it('anexa Authorization quando há sessão', () => {
      comSessao();
      http.get('/api/contatos').subscribe();

      const req = httpMock.expectOne('/api/contatos');
      expect(req.request.headers.get('Authorization')).toBe('Bearer token-de-teste');
      req.flush({});
    });

    it('não anexa nada quando não há sessão', () => {
      http.get('/api/contatos').subscribe();

      const req = httpMock.expectOne('/api/contatos');
      expect(req.request.headers.has('Authorization')).toBeFalse();
      req.flush({});
    });

    it('NÃO manda token nos fluxos públicos, mesmo com um token velho guardado', () => {
      // ===================== POR QUE ISTO É REGRA =====================
      // Login, aceite de convite e redefinição acontecem sem sessão. Mandar um token expirado
      // junto faz a API recusar por expiração — e o sintoma é "não consigo entrar" logo depois
      // de a sessão vencer, que ninguém liga a esta linha.
      // ===============================================================
      comSessao();

      for (const url of ['/api/auth/login', '/api/convite/abc', '/api/redefinir/xyz']) {
        http.post(url, {}).subscribe();
        const req = httpMock.expectOne(url);
        expect(req.request.headers.has('Authorization'))
          .withContext(`${url} não pode levar token`).toBeFalse();
        req.flush({});
      }
    });
  });

  describe('401', () => {
    it('derruba a sessão e manda para o login', () => {
      comSessao();
      expect(auth.autenticado()).toBeTrue();

      http.get('/api/contatos').subscribe({ error: () => { } });
      httpMock.expectOne('/api/contatos').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(auth.autenticado()).toBeFalse();
      expect(localStorage.getItem(CHAVE_TOKEN)).toBeNull();
      expect(localStorage.getItem(CHAVE_USUARIO)).toBeNull();
      expect(navegou).toEqual([['/entrar']]);
    });

    it('401 no LOGIN não derruba nada — é só senha errada', () => {
      // Tratar isto como sessão expirada mandaria o usuário para a tela em que ele já está,
      // limpando o que ele digitou. A mensagem de erro é da tela de login.
      http.post('/api/auth/login', {}).subscribe({ error: () => { } });
      httpMock.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(navegou).toEqual([]);
    });

    it('propaga o erro para quem chamou', () => {
      // Objeto acumulador em vez de `let`: o TypeScript estreita uma variável atribuída só
      // dentro de callback para o tipo do valor inicial, e o `expect` não compilaria.
      const capturado: { status?: number } = {};
      http.get('/api/contatos').subscribe({ error: e => { capturado.status = e.status; } });
      httpMock.expectOne('/api/contatos').flush(null, { status: 401, statusText: 'Unauthorized' });

      // O interceptor reage, mas não engole: a tela ainda precisa saber que falhou.
      expect(capturado.status).toBe(401);
    });
  });

  describe('429', () => {
    it('dispara a contagem regressiva com o Retry-After do login', () => {
      http.post('/api/auth/login', {}).subscribe({ error: () => { } });
      httpMock.expectOne('/api/auth/login').flush(null, {
        status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '42' }
      });

      expect(throttle.segundos()).toBe(42);
    });

    it('sem Retry-After legível, usa 60 em vez de zero', () => {
      // Zero destravaria o botão na hora e o usuário levaria outro 429 — pior que esperar.
      http.post('/api/auth/login', {}).subscribe({ error: () => { } });
      httpMock.expectOne('/api/auth/login')
        .flush(null, { status: 429, statusText: 'Too Many Requests' });

      expect(throttle.segundos()).toBe(60);
    });

    it('429 fora do login não mexe na contagem do botão de entrar', () => {
      http.get('/api/contatos').subscribe({ error: () => { } });
      httpMock.expectOne('/api/contatos')
        .flush(null, { status: 429, statusText: 'Too Many Requests', headers: { 'Retry-After': '30' } });

      expect(throttle.segundos()).toBe(0);
    });
  });

  it('deixa passar o que deu certo, sem tocar na resposta', () => {
    comSessao();
    const capturado: { corpo?: unknown } = {};
    http.get('/api/contatos').subscribe(r => { capturado.corpo = r; });
    httpMock.expectOne('/api/contatos').flush({ itens: [1, 2, 3] });

    expect(capturado.corpo).toEqual({ itens: [1, 2, 3] });
    expect(navegou).toEqual([]);
  });
});
