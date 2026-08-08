import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Captacao } from './captacao';

/** CAPTAÇÃO — os canais de QR Code e link.
 *
 *  ===================== O FORMULÁRIO DO SITE SAIU DA TELA =====================
 *  Ele exigia duas coisas que o cliente típico desta ferramenta não tem: um site, e alguém que
 *  cole HTML nele. O QR responde a mesma pergunta — "de onde veio esse cliente?" — com um adesivo
 *  no balcão.
 *
 *  ⚠️ A API E A TABELA CONTINUAM DE PÉ, e isso é parte da decisão: quem já colou o código num
 *  site em produção continua recebendo lead, e `/formularios` segue acessível pela URL direta
 *  para poder desligar um formulário que esteja no ar. O que saiu foi a porta de entrada.
 *
 *  Este arquivo protege que a tela NÃO volte a pedir a lista de formulários — o resumo antigo
 *  fazia `forkJoin` das duas, e reintroduzir aquela chamada traria a aba de volta por acidente.
 *  ========================================================================== */
describe('captação — os canais de QR', () => {
  const CANAL = {
    id: 1, nome: 'Balcão da loja', codigo: 'k7m2', conexaoId: 10, conexaoNome: 'Principal',
    numero: '5584988887777', origem: 'qrcode', ativo: true, leadsRecebidos: 70,
    mensagem: null,
    link: 'https://wa.me/5584988887777?text=x', texto: 'Olá! Tenho interesse. #k7m2',
    nomeArquivo: 'nexora-balcao-k7m2', podeRemover: true, motivoNaoRemove: null,
    criadoEm: '2026-08-01T10:00:00Z'
  };

  const CANAIS = { itens: [CANAL], conexoes: [], podeCriar: false, leadsAtribuidos: 70 };

  let http: HttpTestingController;
  let fixture: ComponentFixture<Captacao>;
  let c: Captacao;

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Captacao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      r.flush(r.request.url.includes('/canais') ? CANAIS : []);
    }
    fixture.detectChanges();
  }

  afterEach(() => localStorage.clear());

  it('o resumo mostra os números dos canais', () => {
    montar();

    expect(c.leadsCanais()).toBe(70);
    expect(c.totalCanais()).toBe(1);
    expect(c.canaisAtivos()).toBe(1);
    expect(c.carregandoResumo()).toBeFalse();
  });

  /** ⚠️ O TESTE QUE IMPEDE A VOLTA POR ACIDENTE. O resumo antigo buscava as duas listas num
   *  `forkJoin`; quem reintroduzir aquela chamada traz a aba de volta sem querer. */
  it('NAO PEDE a lista de formulários', () => {
    montar();
    http.expectNone(r => r.url.includes('/formularios'));
  });

  it('não há aba nenhuma — sobrou um painel só', () => {
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    // Uma aba única é um controle que não controla nada.
    expect(raiz.querySelectorAll('[role="tab"]').length).toBe(0);
    expect(raiz.querySelector('app-canais')).withContext('o painel de canais está lá').not.toBeNull();
    expect(raiz.querySelector('app-formularios')).toBeNull();
  });

  it('lista que falha vira resumo zerado, não tela presa em "Carregando…"', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(Captacao);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      r.flush({ erro: 'falhou' }, { status: 500, statusText: 'Erro' });
    }
    fixture.detectChanges();

    expect(c.carregandoResumo()).toBeFalse();
    expect(c.leadsCanais()).toBe(0);
  });
});
