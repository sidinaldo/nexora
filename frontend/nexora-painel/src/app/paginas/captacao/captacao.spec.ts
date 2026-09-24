import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Captacao } from './captacao';

/** CAPTAÇÃO — QR Code, link e o formulário do site.
 *
 *  ===================== O FORMULÁRIO VOLTOU, E O QR CONTINUA SENDO O PADRÃO (INT-4) =====================
 *  Ele havia saído da tela por uma razão boa: exige duas coisas que o cliente típico desta
 *  ferramenta não tem — um site, e alguém que cole HTML nele. Isso não mudou, e é por isso que a
 *  aba que ABRE é a do QR.
 *
 *  O que mudou é o que o formulário passou a valer: é ele que carrega o rastro do anúncio
 *  (`utm_*`, `fbclid`, os cookies do pixel) para dentro do CRM.
 *
 *  ⚠️ E o comentário que este arquivo trazia estava ERRADO: ele afirmava que `/formularios`
 *  seguia acessível pela URL direta "para poder desligar um formulário que esteja no ar". Não
 *  seguia — a rota era um redirecionamento para `/captacao`, e o painel não era renderizado em
 *  lugar nenhum. Quem tinha formulário no ar não conseguia ver a chave nem desligá-lo.
 *  ==================================================================================================== */
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

  const FORMULARIOS = [{
    id: 5, nome: 'Landing da promoção', chave: 'a'.repeat(48), dominioPermitido: 'cliente.com.br',
    ativo: true, leadsRecebidos: 30, criadoEm: '2026-08-01T10:00:00Z'
  }];

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
      r.flush(r.request.url.includes('/canais') ? CANAIS : FORMULARIOS);
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

  it('o resumo mostra os números dos DOIS caminhos, para dar para comparar', () => {
    montar();

    expect(c.leadsFormularios()).toBe(30);
    expect(c.totalFormularios()).toBe(1);
    expect(c.total()).toBe(100);

    // A fatia é o que faz o número responder "qual vale a pena repetir".
    expect(c.fatiaCanais()).toBe(70);
    expect(c.fatiaFormularios()).toBe(30);
  });

  it('A ABA QUE ABRE É A DO QR, e o painel de formulários não vem carregado', () => {
    // ⚠️ A ORDEM É DECISÃO DE PRODUTO, não estética: a maioria destes clientes não tem site.
    // Abrir em "Formulário do site" mandaria a padaria para a aba que ela nunca vai usar.
    montar();
    const raiz = fixture.nativeElement as HTMLElement;

    expect(c.aba()).toBe('qr');
    expect(raiz.querySelectorAll('[role="tab"]').length).toBe(2);
    expect(raiz.querySelector('app-canais')).not.toBeNull();

    // `@if` e não CSS: a aba fechada não fica com requisição pendente nem com QR na memória.
    expect(raiz.querySelector('app-formularios')).toBeNull();
  });

  it('TROCAR PARA A ABA DE FORMULÁRIOS mostra o painel dele', () => {
    // É o gesto que este commit existe para devolver: quem publicou um formulário precisa chegar
    // na chave, no botão de desligar e no código novo.
    montar();

    c.trocarAba('formularios');
    fixture.detectChanges();

    const raiz = fixture.nativeElement as HTMLElement;
    expect(raiz.querySelector('app-formularios')).not.toBeNull();
    expect(raiz.querySelector('app-canais')).toBeNull();
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
