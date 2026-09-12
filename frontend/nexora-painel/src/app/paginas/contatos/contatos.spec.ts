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
