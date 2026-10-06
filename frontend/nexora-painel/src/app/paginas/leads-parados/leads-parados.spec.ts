import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { PERMISSOES_DE } from '../../nucleo/seguranca/permissoes-de-teste';
import { LeadParado, PaginaLeadsParados } from '../../nucleo/servicos/leads-parados.servico';
import { LeadsParados } from './leads-parados';

/** ===================== LEADS PARADOS (LPA-1) =====================
 *
 *  ⚠️ O QUE ESTA SUÍTE GUARDA É A HONESTIDADE DA LEITURA. Três jeitos de a tela mentir sem errar
 *  conta nenhuma:
 *
 *    · lead sem negócio aberto desenhado como se faltasse dado, em vez de travessão;
 *    · um vazio só, que manda esperar quando o problema é o filtro;
 *    · filtro trocado sem voltar à página 1 — a lista some e parece "não há nada".
 *  ============================================================== */
describe('leads parados (LPA-1)', () => {
  let fixture: ComponentFixture<LeadsParados>;
  let c: LeadsParados;
  let http: HttpTestingController;

  function lead(over: Partial<LeadParado> = {}): LeadParado {
    return {
      contatoId: 7, nome: 'Joana Prado', telefone: '5584999990000', origem: 'instagram',
      responsavelId: 3, responsavelNome: 'Ana Souza',
      negociacaoId: 41, pipelineNome: 'Vendas', etapaNome: 'Proposta',
      valor: 2500, paradoDesde: '2026-06-01T10:00:00Z', diasParado: 66,
      ...over
    };
  }

  const CHEIA: PaginaLeadsParados = { itens: [lead()], total: 1 };

  const OPCOES = {
    responsaveis: [{ id: 3, nome: 'Ana Souza' }, { id: 4, nome: 'Bruno Lima' }],
    etapas: [], motivosPerda: []
  };

  function montar(papel: 'dono' | 'vendedor' = 'dono', corpo: PaginaLeadsParados = CHEIA) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel,
        permissoes: PERMISSOES_DE[papel], empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/opcoes')) r.flush(OPCOES);
      else r.flush(corpo);
    }
    fixture.detectChanges();
  }

  function raiz(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
  });

  // ==================================================================== a lista

  it('A TABELA MOSTRA O LEAD COM O TEMPO PARADO EM TEXTO', () => {
    montar();

    const linha = raiz().querySelector('.tabela tbody tr')!;

    expect(linha.textContent).toContain('Joana Prado');
    expect(linha.textContent).toContain('Vendas');
    expect(linha.textContent).toContain('Proposta');
    // "há 97 dias" é preciso e não responde "esta pessoa é um caso perdido?".
    expect(linha.querySelector('.parado')!.textContent!.trim()).toBe('há 2 meses');
  });

  it('O TEMPO PARADO VIRA TEXTO QUE SE LÊ, do dia ao mês', () => {
    montar();

    expect(c.tempoParado(0)).toBe('hoje');
    expect(c.tempoParado(1)).toBe('ontem');
    expect(c.tempoParado(12)).toBe('há 12 dias');
    expect(c.tempoParado(30)).toBe('há 1 mês');
    expect(c.tempoParado(95)).toBe('há 3 meses');
  });

  /** ⚠️ SEM NEGÓCIO ABERTO NÃO É DADO FALTANDO. É o lead que entrou por formulário ou importação e
   *  ninguém abriu negócio — o caso mais frio que existe, e o que mais precisa aparecer. Desenhar
   *  célula vazia faria parecer defeito da tela. */
  it('LEAD SEM NEGÓCIO ABERTO DIZ ISSO EM PALAVRAS, e não em célula vazia', () => {
    montar('dono', {
      itens: [lead({ negociacaoId: null, pipelineNome: null, etapaNome: null, valor: null })],
      total: 1
    });

    const linha = raiz().querySelector('.tabela tbody tr')!;

    expect(linha.textContent).toContain('sem negócio aberto');
    expect(linha.querySelectorAll('td')[5].textContent!.trim()).toBe('—');
  });

  // ==================================================================== os filtros

  it('A JANELA TROCA E REFAZ A REQUISIÇÃO, pedindo os dias clicados', () => {
    montar();

    const abas = [...raiz().querySelectorAll('.abas .aba')] as HTMLButtonElement[];
    expect(abas.map(b => b.textContent!.trim())).toEqual(['15 dias', '30 dias', '60 dias', '90 dias']);
    expect(abas[1].getAttribute('aria-pressed')).withContext('30 é o padrão').toBe('true');

    abas[3].click();
    fixture.detectChanges();

    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('dias')).toBe('90');
    req.flush(CHEIA);
  });

  /** ⚠️ TROCAR FILTRO VOLTA PARA A PÁGINA 1. Continuar na página 7 de um recorte que agora tem
   *  duas devolveria uma lista vazia — e o vazio diz "nenhum lead parado", que é mentira. */
  it('TROCAR O FILTRO VOLTA PARA A PRIMEIRA PÁGINA', () => {
    montar('dono', { itens: [lead()], total: 400 });

    c.irPara(5);
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush({ itens: [lead()], total: 400 });
    fixture.detectChanges();
    expect(c.pagina()).toBe(5);

    c.trocarJanela(60);
    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('pagina')).toBe('1');
    req.flush(CHEIA);
    fixture.detectChanges();

    expect(c.pagina()).toBe(1);
  });

  it('O SELETOR DE RESPONSÁVEL MANDA O ID, e "Todos" não manda nada', () => {
    montar();

    c.trocarResponsavel('4');
    let req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('responsavelId')).toBe('4');
    req.flush(CHEIA);

    c.trocarResponsavel('');
    req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.has('responsavelId'))
      .withContext('parâmetro vazio faria o servidor recusar').toBeFalse();
    req.flush(CHEIA);
  });

  /** O seletor vem travado para quem só pode ver a si mesmo — e a trava vem da API, que devolve um
   *  responsável só em `/opcoes`. A tela não decide isso. */
  it('COM UM RESPONSÁVEL SÓ, O SELETOR FICA TRAVADO', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Bia', email: 'b@x.com', papel: 'vendedor',
        permissoes: PERMISSOES_DE.vendedor, empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) {
      if (r.request.url.endsWith('/opcoes')) {
        r.flush({ responsaveis: [{ id: 1, nome: 'Bia' }], etapas: [], motivosPerda: [] });
      } else r.flush(CHEIA);
    }
    fixture.detectChanges();

    expect((raiz().querySelector('#f-responsavel') as HTMLSelectElement).disabled).toBeTrue();
  });

  // ==================================================================== o vazio

  /** ⚠️ DOIS VAZIOS, PORQUE MANDAM FAZER COISAS DIFERENTES: um diz que está tudo em dia, o outro
   *  diz que o recorte é apertado. Um texto único faria o dono com um filtro ligado concluir que
   *  não há lead parado nenhum. */
  it('SEM FILTRO, O VAZIO DIZ QUE ESTÁ TUDO EM DIA', () => {
    montar('dono', { itens: [], total: 0 });

    const vazio = raiz().querySelector('.vazio')!.textContent!;
    expect(vazio).toContain('Nenhum lead parado há mais de 30 dias');
    expect(vazio).toContain('movimento recente');
    expect(raiz().querySelector('.tabela')).toBeNull();
  });

  it('COM FILTRO, O VAZIO MANDA AFROUXAR O RECORTE', () => {
    montar('dono', { itens: [], total: 0 });

    c.trocarResponsavel('4');
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush({ itens: [], total: 0 });
    fixture.detectChanges();

    const vazio = raiz().querySelector('.vazio')!.textContent!;
    expect(vazio).toContain('com esses filtros');
    expect(vazio).toContain('tire o recorte');
  });

  it('O BOTÃO LIMPAR SÓ APARECE COM FILTRO, e devolve ao padrão', () => {
    montar();

    expect(raiz().querySelector('.filtros .btn-neutro')).toBeNull();

    c.trocarJanela(90);
    http.expectOne(r => r.url.endsWith('/leads-parados')).flush(CHEIA);
    fixture.detectChanges();

    const limpar = raiz().querySelector('.filtros .btn-neutro') as HTMLButtonElement;
    expect(limpar).not.toBeNull();

    limpar.click();
    const req = http.expectOne(r => r.url.endsWith('/leads-parados'));
    expect(req.request.params.get('dias')).toBe('30');
    req.flush(CHEIA);
    fixture.detectChanges();

    expect(c.dias()).toBe(30);
    expect(c.responsavelId()).toBeNull();
  });

  // ==================================================================== a paginação

  it('COM UMA PÁGINA SÓ, A PAGINAÇÃO NÃO APARECE', () => {
    montar();
    expect(raiz().querySelector('app-paginacao button')).toBeNull();
  });

  /** O par. Sem ele, uma versão que escondesse a paginação SEMPRE passaria no teste de cima. */
  it('COM MAIS DE UMA PÁGINA, A PAGINAÇÃO APARECE', () => {
    montar('dono', { itens: [lead()], total: 400 });
    expect(raiz().querySelector('app-paginacao button')).not.toBeNull();
  });

  it('FALHA DE REDE MOSTRA RECADO, e não uma tela em branco', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]),
        provideHttpClient(), provideHttpClientTesting()
      ]
    });
    TestBed.inject(AuthServico).aplicarLogin({
      token: 'tok',
      usuario: {
        id: 1, nome: 'Ana', email: 'a@x.com', papel: 'dono',
        permissoes: PERMISSOES_DE.dono, empresaNome: 'Padaria'
      }
    } as never);

    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LeadsParados);
    c = fixture.componentInstance;
    fixture.detectChanges();

    for (const r of http.match(() => true)) r.error(new ProgressEvent('erro'));
    fixture.detectChanges();

    expect(raiz().querySelector('.erro')!.textContent).toContain('Não foi possível carregar');
  });
});
