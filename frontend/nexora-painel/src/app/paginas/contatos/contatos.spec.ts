import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { RealtimeServico } from '../../nucleo/servicos/realtime.servico';
import { RealtimeFalso, rotaFalsa } from '../telas-do-painel';
import { Contatos } from './contatos';

/** A LISTA DE CONTATOS, e especificamente o filtro por etapa.
 *
 *  ===================== POR QUE ESTA SUÍTE NASCEU =====================
 *  A tela não tinha spec nenhuma — só entrava nas suítes genéricas de render e largura, que
 *  provam que ela DESENHA, não que ela funciona.
 *
 *  E ela tinha um defeito que viveu blocos inteiros: `funil.quadro(1)`, escrito quando o primeiro
 *  parâmetro era `porColuna` e `1` queria dizer "um card por coluna". Quando `pipeline` entrou na
 *  FRENTE da assinatura, a chamada continuou compilando e passou a pedir a pipeline de id 1.
 *
 *  Funcionava por acidente na PRIMEIRA empresa, cuja pipeline é justamente a de id 1. Em qualquer
 *  outra o filtro ficava vazio — sem erro, sem log, e sem como filtrar por etapa.
 *  ==================================================================== */
describe('contatos — o filtro por etapa', () => {
  let http: HttpTestingController;

  const FUNIS = [
    { id: 7, nome: 'Vendas', cor: '#1E4028', ordem: 1, padrao: true, etapas: 2, contatos: 3 },
    { id: 9, nome: 'Pós-venda', cor: '#7FA88B', ordem: 2, padrao: false, etapas: 1, contatos: 0 }
  ];

  function coluna(etapaId: number, nome: string) {
    return {
      etapaId, nome, ordem: 1, cor: '#7FA88B', eGanho: false,
      total: 0, valorTotal: 0, concluidas: 0, contatos: [], temMais: false
    };
  }

  function montar() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: RealtimeServico, useClass: RealtimeFalso },
        { provide: ActivatedRoute, useValue: rotaFalsa() }
      ]
    });

    TestBed.inject(AuthServico).aplicarLogin({
      token: 't',
      // VENDEDOR de propósito: o dono dispara `/equipe` a mais, e este teste é sobre o filtro.
      usuario: { id: 1, nome: 'Ana', email: 'a@a.com', papel: 'vendedor', empresaNome: 'X' }
    } as never);

    const fixture = TestBed.createComponent(Contatos);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('PEDE AS ETAPAS DE TODOS OS FUNIS, E NÃO DE UM FIXO', () => {
    const fixture = montar();

    // A lista de contatos em si não interessa aqui.
    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush({ itens: [], temMais: false, total: 0 }));

    // ⚠️ Nenhum dos dois funis tem id 1: com 1 na lista, o defeito antigo passaria.
    http.expectOne(r => r.url.endsWith('/pipelines')).flush(FUNIS);
    fixture.detectChanges();

    const quadros = http.match(r => r.url.includes('/funil'));
    expect(quadros.length).withContext('um por funil').toBe(2);

    const urls = quadros.map(q => q.request.urlWithParams).sort();
    expect(urls[0]).toContain('pipeline=7');
    expect(urls[1]).toContain('pipeline=9');

    // E UM card por coluna: o filtro quer os NOMES das etapas, não o quadro inteiro.
    expect(urls[0]).toContain('porColuna=1');
    expect(urls[1]).toContain('porColuna=1');

    quadros[0].flush({ colunas: [coluna(10, 'Novo Lead'), coluna(11, 'Venda')] });
    quadros[1].flush({ colunas: [coluna(20, 'Entrada')] });
    fixture.detectChanges();

    // As etapas chegam AGRUPADAS por funil: "Proposta" de Vendas e "Proposta" de Pós-venda são
    // etapas diferentes, e sem o grupo o dono escolheria a errada sem perceber.
    const grupos = [...(fixture.nativeElement as HTMLElement).querySelectorAll('optgroup')];
    expect(grupos.map(g => g.label)).toEqual(['Vendas', 'Pós-venda']);
    expect(grupos[0].querySelectorAll('option').length).toBe(2);
    expect(grupos[1].querySelectorAll('option').length).toBe(1);
  });

  /** ⚠️ O LEAD DA CAIXA NAO TEM ETAPA (E6), e a tela dizia "em aberto" sobre ele — o mesmo
   *  rotulo de quem tem negocio aberto. Eram tres estados onde ha quatro. */
  it('contato SEM negociação não é rotulado como "em aberto"', () => {
    const fixture = montar();
    const c = fixture.componentInstance;

    expect(c.situacao({ etapaId: null, ganhoEm: null, perdidoEm: null } as never))
      .toBe('sem-negocio');

    // E os outros tres continuam como eram.
    expect(c.situacao({ etapaId: 5, ganhoEm: '2026-08-01', perdidoEm: null } as never)).toBe('ganho');
    expect(c.situacao({ etapaId: 5, ganhoEm: null, perdidoEm: '2026-08-01' } as never)).toBe('perdido');
    expect(c.situacao({ etapaId: 5, ganhoEm: null, perdidoEm: null } as never)).toBe('aberto');
  });

  /** ===================== O CONTATO QUE SUMIU DA LISTA =====================
   *  Relatado assim: "fechei os cards da Ysia em todos os funis e o contato dele sumiu da lista
   *  de contato". Ela não tinha sumido — a tela abria em "Em aberto", e quem fecha todos os
   *  negócios sai dessa aba. Estava em "Ganhos", uma aba ao lado, sem nada apontando para lá.
   *
   *  Duas coisas consertam isso, e as duas estão aqui: a tela abre no diretório inteiro, e cada
   *  aba diz quantos tem.
   *  ======================================================================== */
  it('ABRE EM "TODOS", E CADA ABA DIZ QUANTOS TEM', () => {
    const fixture = montar();

    const pedido = http.expectOne(r => r.url.includes('/contatos'));

    // ⚠️ `filtro=Todos` NA PRIMEIRA CHAMADA. Era `Abertos`, e é o que escondia o cliente.
    expect(pedido.request.params.get('filtro')).toBe('Todos');

    pedido.flush({
      total: 15, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 13, ganhos: 2, perdidos: 0, todos: 15 }
    });
    fixture.detectChanges();

    const abas = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.aba')]
      .map(a => a.textContent!.replace(/\s+/g, ' ').trim());

    // O zero de "Perdidos" APARECE: esconder deixaria a aba parecendo não-carregada, e zero é
    // uma resposta — "não há ninguém ali".
    expect(abas).toEqual(['Em aberto 13', 'Ganhos 2', 'Perdidos 0', 'Todos 15']);
  });

  /** ⚠️ O ESTADO VAZIO MENTIA SOBRE UMA BASE CHEIA.
   *
   *  `temFiltro()` era `filtro() !== 'Abertos'`, escrito como atalho para "o usuário escolheu
   *  algo". Numa base em que todos já compraram, a aba padrão ficava vazia e a tela dizia
   *  "Nenhum contato ainda. Cadastre um contato ou aguarde alguém mandar mensagem no WhatsApp" —
   *  mandando cadastrar gente para quem já tinha a base inteira. */
  it('ABA VAZIA NÃO DIZ QUE A BASE ESTÁ VAZIA', () => {
    const fixture = montar();
    const c = fixture.componentInstance;

    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 15, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 0, ganhos: 15, perdidos: 0, todos: 15 }
    });
    fixture.detectChanges();

    // Numa aba de estado, o vazio é do RECORTE — e o texto manda voltar para "Todos".
    c.trocarFiltro('Abertos');
    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 0, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 0, ganhos: 15, perdidos: 0, todos: 15 }
    });
    fixture.detectChanges();

    const vazio = () =>
      (fixture.nativeElement as HTMLElement).querySelector('.vazio')!.textContent!;

    expect(c.temFiltro()).toBeTrue();
    expect(vazio()).toContain('Nenhum contato com esses filtros');
    expect(vazio()).not.toContain('Nenhum contato ainda');

    // E em "Todos" vazio — a ÚNICA leitura em que a base está mesmo vazia — a frase volta.
    c.trocarFiltro('Todos');
    http.expectOne(r => r.url.includes('/contatos')).flush({
      total: 0, numeroPagina: 1, tamanho: 30, itens: [],
      contagens: { abertos: 0, ganhos: 0, perdidos: 0, todos: 0 }
    });
    fixture.detectChanges();

    expect(c.temFiltro()).toBeFalse();
    expect(vazio()).toContain('Nenhum contato ainda');
  });

  it('FUNIL SEM ETAPA NÃO VIRA GRUPO VAZIO NO SELETOR', () => {
    // Um `<optgroup>` sem opção aparece como um rótulo morto que não dá para escolher.
    const fixture = montar();

    http.match(r => r.url.includes('/contatos')).forEach(r => r.flush({ itens: [], temMais: false, total: 0 }));
    http.expectOne(r => r.url.endsWith('/pipelines')).flush(FUNIS);
    fixture.detectChanges();

    const quadros = http.match(r => r.url.includes('/funil'));
    quadros[0].flush({ colunas: [coluna(10, 'Novo Lead')] });
    quadros[1].flush({ colunas: [] });
    fixture.detectChanges();

    const grupos = [...(fixture.nativeElement as HTMLElement).querySelectorAll('optgroup')];
    expect(grupos.map(g => g.label)).toEqual(['Vendas']);
  });
});
