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

  const CANAIS = { itens: [CANAL], conexoes: [], podeCriar: false, leadsAtribuidos: 70, semNumero: 0 };

  const FORMULARIOS = [{
    id: 5, nome: 'Landing da promoção', chave: 'a'.repeat(48), dominioPermitido: 'cliente.com.br',
    ativo: true, leadsRecebidos: 30, criadoEm: '2026-08-01T10:00:00Z'
  }];

  /** O resumo como o servidor o manda (AUD-XX). Os números NÃO saem das listas acima de propósito:
   *  se a tela voltar a somar canais e formulários, ela mostra 100 e 70%, não 120 e 62,5%. */
  const RESUMO = {
    leadsTotal: 120, leadsCanais: 75, leadsFormularios: 45,
    percentualCanais: 62.5, percentualFormularios: 37.5,
    canaisAtivos: 1, totalCanais: 2, formulariosAtivos: 1, totalFormularios: 1,
    leadsDeAnuncioSemEnvio: 0
  };

  let http: HttpTestingController;
  let fixture: ComponentFixture<Captacao>;
  let c: Captacao;

  function montar(resumo: Partial<typeof RESUMO> = {}) {
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
      // O resumo é uma rota só (AUD-XX); `/canais` e `/formularios` são os painéis das abas.
      if (r.request.url.endsWith('/captacao/resumo')) r.flush({ ...RESUMO, ...resumo });
      else if (r.request.url.includes('/canais')) r.flush(CANAIS);
      else r.flush(FORMULARIOS);
    }
    fixture.detectChanges();
  }

  afterEach(() => localStorage.clear());

  it('o resumo mostra os números dos canais, do servidor', () => {
    montar();

    expect(c.leadsCanais()).toBe(75);
    expect(c.totalCanais()).toBe(2);
    expect(c.canaisAtivos()).toBe(1);
    expect(c.carregandoResumo()).toBeFalse();
  });

  it('o resumo mostra os números dos DOIS caminhos, para dar para comparar', () => {
    montar();

    expect(c.leadsFormularios()).toBe(45);
    expect(c.totalFormularios()).toBe(1);
    expect(c.total()).toBe(120);

    // A fatia é o que faz o número responder "qual vale a pena repetir" — e vem pronta.
    expect(c.fatiaCanais()).toBe(62.5);
    expect(c.fatiaFormularios()).toBe(37.5);
    const texto = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(texto).toContain('62,5%');
    expect(texto).toContain('37,5%');
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

  // ==================================================================== o anúncio se perdendo
  it('O AVISO DE ANÚNCIO aparece com número, para quem NÃO está enviando', () => {
    // ⚠️ AQUI, e não só no passo de "Primeiros passos": aquele painel some depois que o dono o
    // fecha, e quem já é cliente há meses nunca mais o vê. Esta é a tela onde ele pensa em "de onde
    // vêm meus leads".
    montar({ leadsDeAnuncioSemEnvio: 12 });

    expect(c.leadsComAnuncioPerdidos()).toBe(12);

    const texto = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(texto).toContain('12');
    expect(texto).toContain('a Meta não ficou sabendo');
    expect(texto).toContain('Conectar anúncios');
  });

  /** Para quem já está enviando, o servidor manda zero: dizer "você está perdendo 12 leads" a quem
   *  conectou seria mentira (AUD-XX: a regra saiu da tela, ver `ServicoCaptacao`). */
  it('e NÃO aparece quando o servidor manda zero', () => {
    montar({ leadsDeAnuncioSemEnvio: 0 });
    expect((fixture.nativeElement as HTMLElement).querySelector('.perdendo')).toBeNull();
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
